﻿/*
Copyright (c) 2019, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Linq;
using System.Threading.Tasks;
using MatterControlLib;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.Library.Widgets;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public class SearchPanel : VerticalResizeContainer
	{
		private ChromeTabs tabControl;
		private GuiWidget searchButton;
		private TextEditWithInlineCancel searchBox;

		public SearchPanel(ChromeTabs tabControl, GuiWidget searchButton, ThemeConfig theme)
			: base(theme, GrabBarSide.Left)
		{
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Absolute;
			this.Width = 500;
			this.Height = 200;
			this.BackgroundColor = theme.SectionBackgroundColor;
			this.tabControl = tabControl;
			this.searchButton = searchButton;

			searchButton.BackgroundColor = theme.SectionBackgroundColor;

			GuiWidget searchResults = null;
			var scrollable = new ScrollableWidget(true)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch
			};

			searchBox = new TextEditWithInlineCancel(theme)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(5, 8, 5, 5)
			};
			searchBox.TextEditWidget.ActualTextEditWidget.EnterPressed += async (s2, e2) =>
			{
				searchResults.CloseChildren();

				searchResults.AddChild(
					new TextWidget("Searching".Localize() + "...", pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
					{
						Margin = 10
					});

				this.Invalidate();

				var searchHits = await Task.Run(() =>
				{
					return HelpIndex.Search(searchBox.TextEditWidget.Text);
				});

				searchResults.CloseChildren();

				foreach (var searchResult in searchHits)
				{
					var resultsRow = new HelpSearchResultRow(searchResult, theme);
					resultsRow.Click += this.ResultsRow_Click;

					searchResults.AddChild(resultsRow);
				}

				if (searchResults.Children.Count == 0)
				{
					searchResults.AddChild(new SettingsRow("No results found".Localize(), null, theme, StaticData.Instance.LoadIcon("StatusInfoTip_16x.png", 16, 16).SetPreMultiply()));
				}

				// Add top border to first child
				if (searchResults.Children.FirstOrDefault() is GuiWidget firstChild)
				{
					searchResults.BorderColor = firstChild.BorderColor;
					searchResults.Border = new BorderDouble(top: 1);
					// firstChild.Border = firstChild.Border.Clone(top: 1); - doesn't work for some reason, pushing border to parent above
				}

				scrollable.TopLeftOffset = Vector2.Zero;
			};
			searchBox.ResetButton.Click += (s2, e2) =>
			{
				searchBox.BackgroundColor = Color.Transparent;
				searchBox.TextEditWidget.Text = "";

				searchResults.CloseChildren();
			};

			this.AddChild(searchBox);

			scrollable.ScrollArea.HAnchor = HAnchor.Stretch;
			scrollable.ScrollArea.VAnchor = VAnchor.Fit;

			this.AddChild(scrollable);
			searchResults = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit
			};
			scrollable.AddChild(searchResults);
		}

		public override void OnLoad(EventArgs args)
		{
			// Set initial focus to input field
			searchBox.TextEditWidget.Focus();

			base.OnLoad(args);
		}

		public override void OnBoundsChanged(EventArgs e)
		{
			if (this.Parent is GuiWidget parent)
			{
				this.Position = new Vector2(parent.Width - this.Width, this.Position.Y);
			}

			base.OnBoundsChanged(e);
		}

		public override void OnClosed(EventArgs e)
		{
			if (searchButton != null)
			{
				searchButton.BackgroundColor = Color.Transparent;
				searchButton = null;
			}

			base.OnClosed(e);
		}

		private void ResultsRow_Click(object sender, MouseEventArgs e)
		{
			ChromeTab helpDocsTab = ApplicationController.Instance.ActivateHelpTab("Docs");

			tabControl.ActiveTab = helpDocsTab;

			if (helpDocsTab.TabContent is HelpTreePanel treePanel)
			{
				treePanel.MatchingText = searchBox.TextEditWidget.Text;
				treePanel.ActiveNodePath = (sender as HelpSearchResultRow).SearchResult.Path;
			}
		}
	}
}