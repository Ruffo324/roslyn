﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Navigation;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices.Implementation.Extensions;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Roslyn.Utilities;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using VsTextSpan = Microsoft.VisualStudio.TextManager.Interop.TextSpan;

namespace Microsoft.VisualStudio.LanguageServices.Implementation
{
    using Workspace = Microsoft.CodeAnalysis.Workspace;

    [ExportWorkspaceService(typeof(IDocumentNavigationService), ServiceLayer.Host), Shared]
    [Export(typeof(VisualStudioDocumentNavigationService))]
    internal sealed class VisualStudioDocumentNavigationService : ForegroundThreadAffinitizedObject, IDocumentNavigationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
        private readonly IVsRunningDocumentTable4 _runningDocumentTable;
        private readonly IThreadingContext _threadingContext;
        private readonly Lazy<SourceGeneratedFileManager> _sourceGeneratedFileManager;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public VisualStudioDocumentNavigationService(
            IThreadingContext threadingContext,
            SVsServiceProvider serviceProvider,
            IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
            Lazy<SourceGeneratedFileManager> sourceGeneratedFileManager /* lazy to avoid circularities */)
            : base(threadingContext)
        {
            _serviceProvider = serviceProvider;
            _editorAdaptersFactoryService = editorAdaptersFactoryService;
            _runningDocumentTable = (IVsRunningDocumentTable4)serviceProvider.GetService(typeof(SVsRunningDocumentTable));
            _threadingContext = threadingContext;
            _sourceGeneratedFileManager = sourceGeneratedFileManager;
        }

        public async Task<bool> CanNavigateToSpanAsync(Workspace workspace, DocumentId documentId, TextSpan textSpan, CancellationToken cancellationToken)
        {
            // Navigation should not change the context of linked files and Shared Projects.
            documentId = workspace.GetDocumentIdInCurrentContext(documentId);

            if (!IsSecondaryBuffer(workspace, documentId))
            {
                return true;
            }

            var document = workspace.CurrentSolution.GetRequiredDocument(documentId);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            var boundedTextSpan = GetSpanWithinDocumentBounds(textSpan, text.Length);
            if (boundedTextSpan != textSpan)
            {
                try
                {
                    throw new ArgumentOutOfRangeException();
                }
                catch (ArgumentOutOfRangeException e) when (FatalError.ReportAndCatch(e))
                {
                }

                return false;
            }

            var vsTextSpan = text.GetVsTextSpanForSpan(textSpan);

            return await CanMapFromSecondaryBufferToPrimaryBufferAsync(
                workspace, documentId, vsTextSpan, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> CanNavigateToLineAndOffsetAsync(Workspace workspace, DocumentId documentId, int lineNumber, int offset, CancellationToken cancellationToken)
        {
            // Navigation should not change the context of linked files and Shared Projects.
            documentId = workspace.GetDocumentIdInCurrentContext(documentId);

            if (!IsSecondaryBuffer(workspace, documentId))
            {
                return true;
            }

            var document = workspace.CurrentSolution.GetRequiredDocument(documentId);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var vsTextSpan = text.GetVsTextSpanForLineOffset(lineNumber, offset);

            return await CanMapFromSecondaryBufferToPrimaryBufferAsync(
                workspace, documentId, vsTextSpan, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> CanNavigateToPositionAsync(Workspace workspace, DocumentId documentId, int position, int virtualSpace, CancellationToken cancellationToken)
        {
            // Navigation should not change the context of linked files and Shared Projects.
            documentId = workspace.GetDocumentIdInCurrentContext(documentId);

            if (!IsSecondaryBuffer(workspace, documentId))
            {
                return true;
            }

            var document = workspace.CurrentSolution.GetRequiredDocument(documentId);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            var boundedPosition = GetPositionWithinDocumentBounds(position, text.Length);
            if (boundedPosition != position)
            {
                try
                {
                    throw new ArgumentOutOfRangeException();
                }
                catch (ArgumentOutOfRangeException e) when (FatalError.ReportAndCatch(e))
                {
                }

                return false;
            }

            var vsTextSpan = text.GetVsTextSpanForPosition(position, virtualSpace);

            return await CanMapFromSecondaryBufferToPrimaryBufferAsync(
                workspace, documentId, vsTextSpan, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> TryNavigateToSpanAsync(Workspace workspace, DocumentId documentId, TextSpan textSpan, NavigationOptions options, bool allowInvalidSpan, CancellationToken cancellationToken)
        {
            return TryNavigateToLocationAsync(workspace,
                documentId,
                _ => Task.FromResult(textSpan),
                text => GetVsTextSpan(text, textSpan, allowInvalidSpan),
                options,
                cancellationToken);

            static VsTextSpan GetVsTextSpan(SourceText text, TextSpan textSpan, bool allowInvalidSpan)
            {
                var boundedTextSpan = GetSpanWithinDocumentBounds(textSpan, text.Length);
                if (boundedTextSpan != textSpan && !allowInvalidSpan)
                {
                    try
                    {
                        throw new ArgumentOutOfRangeException();
                    }
                    catch (ArgumentOutOfRangeException e) when (FatalError.ReportAndCatch(e))
                    {
                    }
                }

                return text.GetVsTextSpanForSpan(boundedTextSpan);
            }
        }

        public Task<bool> TryNavigateToLineAndOffsetAsync(
            Workspace workspace, DocumentId documentId, int lineNumber, int offset, NavigationOptions options, CancellationToken cancellationToken)
        {
            return TryNavigateToLocationAsync(workspace,
                documentId,
                document => GetTextSpanFromLineAndOffsetAsync(document, lineNumber, offset, cancellationToken),
                text => GetVsTextSpan(text, lineNumber, offset),
                options,
                cancellationToken);

            static async Task<TextSpan> GetTextSpanFromLineAndOffsetAsync(Document document, int lineNumber, int offset, CancellationToken cancellationToken)
            {
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

                var linePosition = new LinePosition(lineNumber, offset);
                return text.Lines.GetTextSpan(new LinePositionSpan(linePosition, linePosition));
            }

            static VsTextSpan GetVsTextSpan(SourceText text, int lineNumber, int offset)
            {
                return text.GetVsTextSpanForLineOffset(lineNumber, offset);
            }
        }

        public Task<bool> TryNavigateToPositionAsync(
            Workspace workspace, DocumentId documentId, int position, int virtualSpace, NavigationOptions options, CancellationToken cancellationToken)
        {
            return TryNavigateToLocationAsync(workspace,
                documentId,
                document => GetTextSpanFromPositionAsync(document, position, virtualSpace, cancellationToken),
                text => GetVsTextSpan(text, position, virtualSpace),
                options,
                cancellationToken);

            static async Task<TextSpan> GetTextSpanFromPositionAsync(Document document, int position, int virtualSpace, CancellationToken cancellationToken)
            {
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                text.GetLineAndOffset(position, out var lineNumber, out var offset);

                offset += virtualSpace;

                var linePosition = new LinePosition(lineNumber, offset);
                return text.Lines.GetTextSpan(new LinePositionSpan(linePosition, linePosition));
            }

            static VsTextSpan GetVsTextSpan(SourceText text, int position, int virtualSpace)
            {
                var boundedPosition = GetPositionWithinDocumentBounds(position, text.Length);
                if (boundedPosition != position)
                {
                    try
                    {
                        throw new ArgumentOutOfRangeException();
                    }
                    catch (ArgumentOutOfRangeException e) when (FatalError.ReportAndCatch(e))
                    {
                    }
                }

                return text.GetVsTextSpanForPosition(boundedPosition, virtualSpace);
            }
        }

        private async Task<bool> TryNavigateToLocationAsync(
            Workspace workspace,
            DocumentId documentId,
            Func<Document, Task<TextSpan>> getTextSpanForMappingAsync,
            Func<SourceText, VsTextSpan> getVsTextSpan,
            NavigationOptions options,
            CancellationToken cancellationToken)
        {
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Navigation should not change the context of linked files and Shared Projects.
            documentId = workspace.GetDocumentIdInCurrentContext(documentId);

            using (OpenNewDocumentStateScope(options))
            {
                // Keep this on the UI thread so that the scope disposal stays there.
                return await TryNavigateToLocationUnderScopeAsync(
                    workspace, documentId, getTextSpanForMappingAsync, getVsTextSpan, cancellationToken).ConfigureAwait(true);
            }
        }

        private async Task<bool> TryNavigateToLocationUnderScopeAsync(
            Workspace workspace,
            DocumentId documentId,
            Func<Document, Task<TextSpan>> getTextSpanForMappingAsync,
            Func<SourceText, VsTextSpan> getVsTextSpan,
            CancellationToken cancellationToken)
        {
            var solution = workspace.CurrentSolution;
            var document = solution.GetDocument(documentId);
            if (document == null)
            {
                var project = solution.GetProject(documentId.ProjectId);
                if (project is null)
                {
                    // This is a source generated document shown in Solution Explorer, but is no longer valid since
                    // the configuration and/or platform changed since the last generation completed.
                    return false;
                }

                var generatedDocument = await project.GetSourceGeneratedDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
                if (generatedDocument != null)
                {
                    await _sourceGeneratedFileManager.Value.NavigateToSourceGeneratedFileAsync(
                        generatedDocument,
                        await getTextSpanForMappingAsync(generatedDocument).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                return false;
            }

            // Before attempting to open the document, check if the location maps to a different file that should be opened instead.
            var spanMappingService = document.Services.GetService<ISpanMappingService>();
            if (spanMappingService != null)
            {
                var mappedSpan = await GetMappedSpanAsync(
                    spanMappingService,
                    document,
                    await getTextSpanForMappingAsync(document).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                if (mappedSpan.HasValue)
                {
                    // Check if the mapped file matches one already in the workspace.
                    // If so use the workspace APIs to navigate to it.  Otherwise use VS APIs to navigate to the file path.
                    var documentIdsForFilePath = solution.GetDocumentIdsWithFilePath(mappedSpan.Value.FilePath);
                    if (!documentIdsForFilePath.IsEmpty)
                    {
                        // If the mapped file maps to the same document that was passed in, then re-use the documentId to preserve context.
                        // Otherwise, just pick one of the ids to use for navigation.
                        var documentIdToNavigate = documentIdsForFilePath.Contains(documentId) ? documentId : documentIdsForFilePath.First();
                        return await NavigateToFileInWorkspaceAsync(
                            documentIdToNavigate, workspace, getVsTextSpan, cancellationToken).ConfigureAwait(false);
                    }

                    return await TryNavigateToMappedFileAsync(
                        workspace, document, mappedSpan.Value, cancellationToken).ConfigureAwait(false);
                }
            }

            return await NavigateToFileInWorkspaceAsync(
                documentId, workspace, getVsTextSpan, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> NavigateToFileInWorkspaceAsync(
            DocumentId documentId,
            Workspace workspace,
            Func<SourceText, VsTextSpan> getVsTextSpan,
            CancellationToken cancellationToken)
        {
            // Have to be on the UI thread to open a document.
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var document = OpenDocument(workspace, documentId);
            if (document == null)
            {
                return false;
            }

            // Keep this on the UI thread since we're about to do span mapping.
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(true);
            var textBuffer = text.Container.GetTextBuffer();

            var vsTextSpan = getVsTextSpan(text);
            if (IsSecondaryBuffer(workspace, documentId) &&
                !vsTextSpan.TryMapSpanFromSecondaryBufferToPrimaryBuffer(workspace, documentId, out vsTextSpan))
            {
                return false;
            }

            return NavigateTo(textBuffer, vsTextSpan, cancellationToken);
        }

        private async Task<bool> TryNavigateToMappedFileAsync(
            Workspace workspace, Document generatedDocument, MappedSpanResult mappedSpanResult, CancellationToken cancellationToken)
        {
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var vsWorkspace = (VisualStudioWorkspaceImpl)workspace;
            // TODO - Move to IOpenDocumentService - https://github.com/dotnet/roslyn/issues/45954
            // Pass the original result's project context so that if the mapped file has the same context available, we navigate
            // to the mapped file with a consistent project context.
            vsWorkspace.OpenDocumentFromPath(mappedSpanResult.FilePath, generatedDocument.Project.Id);
            if (_runningDocumentTable.TryGetBufferFromMoniker(_editorAdaptersFactoryService, mappedSpanResult.FilePath, out var textBuffer))
            {
                var vsTextSpan = new VsTextSpan
                {
                    iStartIndex = mappedSpanResult.LinePositionSpan.Start.Character,
                    iStartLine = mappedSpanResult.LinePositionSpan.Start.Line,
                    iEndIndex = mappedSpanResult.LinePositionSpan.End.Character,
                    iEndLine = mappedSpanResult.LinePositionSpan.End.Line
                };

                return NavigateTo(textBuffer, vsTextSpan, cancellationToken);
            }

            return false;
        }

        private static async Task<MappedSpanResult?> GetMappedSpanAsync(
            ISpanMappingService spanMappingService, Document generatedDocument, TextSpan textSpan, CancellationToken cancellationToken)
        {
            var results = await spanMappingService.MapSpansAsync(
                generatedDocument, SpecializedCollections.SingletonEnumerable(textSpan), cancellationToken).ConfigureAwait(false);

            if (!results.IsDefaultOrEmpty)
            {
                return results.First();
            }

            return null;
        }

        /// <summary>
        /// It is unclear why, but we are sometimes asked to navigate to a position that is not
        /// inside the bounds of the associated <see cref="Document"/>. This method returns a
        /// position that is guaranteed to be inside the <see cref="Document"/> bounds. If the
        /// returned position is different from the given position, then the worst observable
        /// behavior is either no navigation or navigation to the end of the document. See the
        /// following bugs for more details:
        ///     https://devdiv.visualstudio.com/DevDiv/_workitems?id=112211
        ///     https://devdiv.visualstudio.com/DevDiv/_workitems?id=136895
        ///     https://devdiv.visualstudio.com/DevDiv/_workitems?id=224318
        ///     https://devdiv.visualstudio.com/DevDiv/_workitems?id=235409
        /// </summary>
        private static int GetPositionWithinDocumentBounds(int position, int documentLength)
            => Math.Min(documentLength, Math.Max(position, 0));

        /// <summary>
        /// It is unclear why, but we are sometimes asked to navigate to a <see cref="TextSpan"/>
        /// that is not inside the bounds of the associated <see cref="Document"/>. This method
        /// returns a span that is guaranteed to be inside the <see cref="Document"/> bounds. If
        /// the returned span is different from the given span, then the worst observable behavior
        /// is either no navigation or navigation to the end of the document.
        /// See https://github.com/dotnet/roslyn/issues/7660 for more details.
        /// </summary>
        private static TextSpan GetSpanWithinDocumentBounds(TextSpan span, int documentLength)
            => TextSpan.FromBounds(GetPositionWithinDocumentBounds(span.Start, documentLength), GetPositionWithinDocumentBounds(span.End, documentLength));

        private static Document? OpenDocument(Workspace workspace, DocumentId documentId)
        {
            // Always open the document again, even if the document is already open in the 
            // workspace. If a document is already open in a preview tab and it is opened again 
            // in a permanent tab, this allows the document to transition to the new state.
            if (workspace.CanOpenDocuments)
            {
                workspace.OpenDocument(documentId);
            }

            if (!workspace.IsDocumentOpen(documentId))
            {
                return null;
            }

            return workspace.CurrentSolution.GetDocument(documentId);
        }

        public bool NavigateTo(ITextBuffer textBuffer, VsTextSpan vsTextSpan, CancellationToken cancellationToken)
        {
            using (Logger.LogBlock(FunctionId.NavigationService_VSDocumentNavigationService_NavigateTo, cancellationToken))
            {
                var vsTextBuffer = _editorAdaptersFactoryService.GetBufferAdapter(textBuffer);
                if (vsTextBuffer == null)
                {
                    Debug.Fail("Could not get IVsTextBuffer for document!");
                    return false;
                }

                var textManager = (IVsTextManager2)_serviceProvider.GetService(typeof(SVsTextManager));
                if (textManager == null)
                {
                    Debug.Fail("Could not get IVsTextManager service!");
                    return false;
                }

                return ErrorHandler.Succeeded(
                    textManager.NavigateToLineAndColumn2(
                        vsTextBuffer,
                        VSConstants.LOGVIEWID.TextView_guid,
                        vsTextSpan.iStartLine,
                        vsTextSpan.iStartIndex,
                        vsTextSpan.iEndLine,
                        vsTextSpan.iEndIndex,
                        (uint)_VIEWFRAMETYPE.vftCodeWindow));
            }
        }

        private static bool IsSecondaryBuffer(Workspace workspace, DocumentId documentId)
        {
            if (workspace is not VisualStudioWorkspaceImpl visualStudioWorkspace)
            {
                return false;
            }

            var containedDocument = visualStudioWorkspace.TryGetContainedDocument(documentId);
            if (containedDocument == null)
            {
                return false;
            }

            return true;
        }

        private async Task<bool> CanMapFromSecondaryBufferToPrimaryBufferAsync(
            Workspace workspace, DocumentId documentId, VsTextSpan spanInSecondaryBuffer, CancellationToken cancellationToken)
        {
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return spanInSecondaryBuffer.TryMapSpanFromSecondaryBufferToPrimaryBuffer(workspace, documentId, out _);
        }

        private static IDisposable OpenNewDocumentStateScope(NavigationOptions options)
        {
            var state = options.PreferProvisionalTab
                ? __VSNEWDOCUMENTSTATE.NDS_Provisional
                : __VSNEWDOCUMENTSTATE.NDS_Permanent;

            if (!options.ActivateTab)
            {
                state |= __VSNEWDOCUMENTSTATE.NDS_NoActivate;
            }

            return new NewDocumentStateScope(state, VSConstants.NewDocumentStateReason.Navigation);
        }
    }
}
