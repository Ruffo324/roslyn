﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.VisualStudio.IntegrationTest.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Roslyn.VisualStudio.IntegrationTests.VisualBasic
{
    [Collection(nameof(SharedIntegrationHostFixture))]
    public class BasicNavigationBar : AbstractEditorTest
    {
        protected override string LanguageName => LanguageNames.VisualBasic;

        public BasicNavigationBar(VisualStudioInstanceFactory instanceFactory)
            : base(instanceFactory, nameof(BasicNavigationBar))
        {
        }

        public override async Task DisposeAsync()
        {
            VisualStudio.Workspace.SetFeatureOption("NavigationBarOptions", "ShowNavigationBar", "Visual Basic", "True");
            await base.DisposeAsync();
        }

        [WpfFact]
        [Trait(Traits.Feature, Traits.Features.NavigationBar)]
        public void VerifyEvents()
        {
            SetUpEditor(@"
$$Class Item1
    Public Event EvA As Action
    Public Event EvB As Action
End Class

Class Item2
    Public Event EvX As Action
    Public Event EvY As Action
End Class

Partial Class C
    WithEvents item1 As Item1
    WithEvents item2 As Item2
End Class

Partial Class C
    Private Sub item1_EvA() Handles item1.EvA
        ' 1
    End Sub

    Private Sub item1_EvB() Handles item1.EvB
        ' 2
    End Sub

    Private Sub item2_EvX() Handles item2.EvX
        ' 3
    End Sub

    Private Sub item2_EvY() Handles item2.EvY
        ' 4
    End Sub
End Class");

            VisualStudio.Editor.PlaceCaret("' 1");

            VerifyLeftSelected("item1");
            VerifyRightSelected("EvA");

            VisualStudio.Editor.PlaceCaret("' 2");

            VerifyLeftSelected("item1");
            VerifyRightSelected("EvB");

            VisualStudio.Editor.PlaceCaret("' 3");

            VerifyLeftSelected("item2");
            VerifyRightSelected("EvX");

            VisualStudio.Editor.PlaceCaret("' 4");

            VerifyLeftSelected("item2");
            VerifyRightSelected("EvY");

            // Selecting an event should update the selected member in the type list.
            VisualStudio.Editor.SelectMemberNavBarItem("EvX");
            VisualStudio.Editor.Verify.CurrentLineText("        $$' 3", assertCaretPosition: true, trimWhitespace: false);

            // Selecting an WithEvents member in the type list should have no impact on position.
            // But it should update the items in the member list.
            VisualStudio.Editor.SelectTypeNavBarItem("item1");
            VisualStudio.Editor.Verify.CurrentLineText("        $$' 3", assertCaretPosition: true, trimWhitespace: false);
            Assert.Equal(new[] { "EvA", "EvB" }, VisualStudio.Editor.GetMemberNavBarItems());
        }

        private void VerifyLeftSelected(string expected)
        {
            Assert.Equal(expected, VisualStudio.Editor.GetTypeNavBarSelection());
        }

        private void VerifyRightSelected(string expected)
        {
            Assert.Equal(expected, VisualStudio.Editor.GetMemberNavBarSelection());
        }
    }
}
