﻿/*
Copyright (c) 2018, Lars Brubaker, John Lewin
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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.GuiAutomation;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.Library.Widgets;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.PrinterCommunication;
using MatterHackers.MatterControl.PrinterCommunication.Io;
using MatterHackers.MatterControl.PrinterControls.PrinterConnections;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.PrinterEmulator;
using Newtonsoft.Json;
using NUnit.Framework;
using SQLiteWin32;

namespace MatterHackers.MatterControl.Tests.Automation
{
	[TestFixture, Category("MatterControl.UI.Automation")]
	public static class MatterControlUtilities
	{
		private static bool saveImagesForDebug = true;

		private static event EventHandler UnregisterEvents;

		private static int testID = 0;

		private static string runName = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss");

		public static string PathToDownloadsSubFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "-Temporary");

		private static SystemWindow rootSystemWindow;

		public static void RemoveAllFromQueue(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Queue... Menu")
				.Delay(1)
				.ClickByName(" Remove All Menu Item");
		}

		public static void CreateDownloadsSubFolder()
		{
			if (Directory.Exists(PathToDownloadsSubFolder))
			{
				foreach (string filePath in Directory.GetFiles(PathToDownloadsSubFolder))
				{
					File.Delete(filePath);
				}
			}
			else
			{
				Directory.CreateDirectory(PathToDownloadsSubFolder);
			}
		}

		public static void DeleteDownloadsSubFolder()
		{
			Directory.Delete(PathToDownloadsSubFolder, true);
		}

		public static void SignOutUser(this AutomationRunner testRunner)
		{
			testRunner.ClickSignOut();

			// Rather than waiting a fixed amount of time, we wait for the ReloadAll to complete before returning
			testRunner.WaitForReloadAll(() => testRunner.ClickByName("Yes Button"));
		}

		public static void ClickSignOut(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("User Options Menu")
				.ClickByName("Sign Out Menu Item")
				.Delay(.5);
		}

		public static AutomationRunner AddPrimitivePartsToBed(this AutomationRunner testRunner, IEnumerable<string> partNames, bool multiSelect = false)
		{
			// Passing in true for multiselect will simulate holding down control when clicking on each part. This will create a
			// selection group that is added to the plate as one scene part.

			// Open the library pane to force display of the overflow menu and add a widget for it to the widget hierarchy.
			// We need this menu widget to trigger the Add to Bed menu item.
			const string containerName = "Primitives Row Item Collection";
			testRunner.NavigateToFolder(containerName);

			if (multiSelect)
			{
				testRunner.PressModifierKeys(AutomationRunner.ModifierKeys.Control);
			}

			var partCount = 0;
			foreach (var partName in partNames)
			{
				testRunner.ScrollIntoView(partName);

				foreach (var result in testRunner.GetWidgetsByName(partName))
				{
					// Opening the primitive parts library folder causes a second set of primitive part widgets to be created.
					// The first set is hidden behind the expanded library pane and targeting them for a click will cause the
					// automation runner to click in the wrong spots. Finding the correct widgets to target is a little complicated
					// because of the layers of wrapping widgets but the widgets in the first set (which we don't want to target)
					// are direct descendents of a ListContentView so we can eliminate those and assume whatever is left over are
					// the widgets we want.

					var partWidget = result.Widget as ListViewItemBase;
					if (partWidget.Parent.Name == "Library ListContentView")
					{
						continue;
					}
					if (!partWidget.IsSelected)
					{
						if (multiSelect)
						{
							testRunner.ClickWidget(partWidget);
						}
						else
						{
							testRunner.RightClickWidget(partWidget)
								.ClickByName("Add to Bed Menu Item");
						}
					}
					partCount += 1;
					break;
				}
			}

			if (multiSelect)
			{
				testRunner.ReleaseModifierKeys(AutomationRunner.ModifierKeys.Control)
					.ClickByName("Print Library Overflow Menu")
					.ClickByName("Add to Bed Menu Item");
			}

			var view3D = testRunner.GetWidgetByName("View3DWidget", out _) as View3DWidget;
			var scene = view3D.Object3DControlLayer.Scene;
			var preAddCount = scene.Children.Count;
			var postAddCount = preAddCount + (multiSelect ? 1 : partCount);

			// wait for the objects to be added
			testRunner.WaitFor(() => scene.Children.Count == postAddCount, 1);

			return testRunner;
		}

		public static void ChangeSettings(this AutomationRunner testRunner,
			IEnumerable<(string key, string value)> settings,
			PrinterConfig printer)
		{
			bool needReload = false;

			foreach (var setting in settings)
			{
				if (printer.Settings.GetValue(setting.key) != setting.value
					&& PrinterSettings.SettingsData[setting.key].UiUpdate != SliceSettingData.UiUpdateRequired.None)
				{
					needReload = true;
					break;
				}
			}

			if (needReload)
			{
				testRunner.WaitForReloadAll(() =>
				{
					foreach (var setting in settings)
					{
						printer.Settings.SetValue(setting.key, setting.value);
					}
				});
			}
			else
			{
				foreach (var setting in settings)
				{
					printer.Settings.SetValue(setting.key, setting.value);
				}
			}
		}

		public static AutomationRunner WaitForReloadAll(this AutomationRunner testRunner, Action reloadAllAction)
		{
			// Wire up a block and release mechanism to wait until the sign in process has completed
			AutoResetEvent resetEvent = new AutoResetEvent(false);
			ApplicationController.Instance.DoneReloadingAll.RegisterEvent((s, e) => resetEvent.Set(), ref UnregisterEvents);

			// Start the procedure that begins a ReloadAll event in MatterControl
			reloadAllAction();

			// Wait up to 10 seconds for the DoneReloadingAll event
			resetEvent.WaitOne(10 * 1000);

			// Remove our DoneReloadingAll listener
			UnregisterEvents(null, null);

			// Wait for any post DoneReloadingAll code to finish up and return
			testRunner.Delay(.2);
			return testRunner;
		}

		public static AutomationRunner WaitForPage(this AutomationRunner testRunner, string headerText)
		{
			// Helper methods
			bool HeaderExists(string text)
			{
				var header = testRunner.GetWidgetByName("HeaderRow", out _);
				var textWidget = header.Children<TextWidget>().FirstOrDefault();

				return textWidget?.Text.StartsWith(text) ?? false;
			}

			testRunner.WaitFor(() => HeaderExists(headerText));

			Assert.IsTrue(HeaderExists(headerText), "Expected page not found: " + headerText);

			return testRunner;
		}

		public static string PathToExportGcodeFolder
		{
			get => TestContext.CurrentContext.ResolveProjectPath(4, "Tests", "TestData", "ExportedGcode", runName);
		}

		public static string GetTestItemPath(string queueItemToLoad)
		{
			return TestContext.CurrentContext.ResolveProjectPath(4, "Tests", "TestData", "QueueItems", queueItemToLoad);
		}

		public static void CloseMatterControl(this AutomationRunner testRunner)
		{
			rootSystemWindow?.Close();
		}

		public enum PrepAction
		{
			CloseSignInAndPrinterSelect
		}
;

		public static AutomationRunner ExpandEditTool(this AutomationRunner testRunner, string expandCheckboxButtonName)
		{
			var mirrorPanel = testRunner.GetWidgetByName(expandCheckboxButtonName, out _);
			var checkBox = mirrorPanel.Children<ExpandCheckboxButton>().FirstOrDefault();
			if (checkBox?.Checked != true)
			{
				testRunner.ClickByName(expandCheckboxButtonName);
			}

			return testRunner;
		}

		public static AutomationRunner Select3DPart(this AutomationRunner testRunner, string partNameToSelect)
		{
			if (testRunner.NameExists("3D View Edit", .2))
			{
				testRunner.ClickByName("3D View Edit");
			}

			return testRunner.ClickByName(partNameToSelect);
		}

		public static AutomationRunner ClickDiscardChanges(this AutomationRunner testRunner)
		{
			return testRunner.ClickByName("No Button");
		}

		public static AutomationRunner WaitForFirstDraw(this AutomationRunner testRunner)
		{
			testRunner.GetWidgetByName("PartPreviewContent", out SystemWindow systemWindow, 10);
			// make sure we wait for MC to be up and running
			testRunner.WaitforDraw(systemWindow);
			return testRunner;
		}

		public static AutomationRunner OpenPartTab(this AutomationRunner testRunner, bool removeDefaultPhil = true)
		{
			SystemWindow systemWindow;
			testRunner.GetWidgetByName("Hardware Tab", out systemWindow, 10);
			testRunner.WaitforDraw(systemWindow)
				// close the welcome message
				.ClickByName("Start New Design")
				.Delay(.5)
				// and close the product tour offer
				.ClickByName("Cancel Wizard Button");

			if (removeDefaultPhil)
			{
				testRunner.VerifyAndRemovePhil();
			}

			return testRunner;
		}

		public static void ChangeToQueueContainer(this AutomationRunner testRunner)
		{
			testRunner.NavigateToFolder("Queue Row Item Collection");
		}

		public class PrintEmulatorProcess : Process
		{
			protected override void Dispose(bool disposing)
			{
				try
				{
					this.Kill();
				}
				catch
				{
				}

				base.Dispose(disposing);
			}
		}

		public static Emulator LaunchAndConnectToPrinterEmulator(this AutomationRunner testRunner,
			string make = "Airwolf 3D",
			string model = "HD",
			bool runSlow = false,
			bool pinSettingsOpen = true)
		{
			var hardwareTab = testRunner.GetWidgetByName("Hardware Tab", out SystemWindow systemWindow, 10);

			// make sure we wait for MC to be up and running
			testRunner.WaitforDraw(systemWindow);

			// Load the TestEnv config
			var config = TestAutomationConfig.Load();

			// Override the heat up time
			Emulator.DefaultHeatUpTime = config.HeatupTime;

			// Override the temp stabilization time
			WaitForTempStream.WaitAfterReachTempTime = config.TempStabilizationTime;

			// Create the printer
			testRunner.AddAndSelectPrinter(make, model)
				.SwitchToPrinterSettings(pinSettingsOpen)
				.GetWidgetByName("com_port Field", out GuiWidget serialPortDropDown, out _)
				// Wait until the serialPortDropDown is ready to click it. Ensures the printer is loaded.
				.WaitFor(() => serialPortDropDown.Enabled)
				.ClickByName("com_port Field")
				.ClickByName("Emulator Menu Item")
				.ClickByName("Connect to printer button") // connect to the created printer
				.WaitForName("Disconnect from printer button");

			// replace the old behavior of clicking the 'Already Loaded' button by setting to filament_has_been_loaded.
			ApplicationController.Instance.ActivePrinters.First().Settings.SetValue(SettingsKey.filament_has_been_loaded, "1");

			// Access through static instance must occur after Connect has occurred and the port has spun up
			Emulator.Instance.RunSlow = runSlow;

			return Emulator.Instance;
		}

		public static AutomationRunner CancelPrint(this AutomationRunner testRunner)
		{
			// If the pause/resume dialog is open, dismiss it before canceling the print
			if (testRunner.NamedWidgetExists("Yes Button"))
			{
				testRunner.ClickByName("Yes Button");
			}

			testRunner.WaitForWidgetEnabled("Print Progress Dial", 15)
				.WaitForWidgetEnabled("Stop Task Button")
				.ClickByName("Stop Task Button")
				.WaitForName("Ok Button", 10); // Wait for and dismiss the new PrintCompleted dialog

			testRunner.ClickByName("Cancel Wizard Button");

			return testRunner;
		}

		public static void WaitForLayer(this Emulator emulator, PrinterSettings printerSettings, int layerNumber, double secondsToWait = 30)
		{
			var resetEvent = new AutoResetEvent(false);

			var heightAtTargetLayer = printerSettings.GetValue<double>(SettingsKey.layer_height) * layerNumber;

			// Wait for emulator to hit target layer
			emulator.DestinationChanged += (s, e) =>
			{
				// Wait for print to start, then slow down the emulator and continue. Failing to slow down frequently causes a timing issue where the print
				// finishes before we make it down to 'CloseMatterControlViaUi' and thus no prompt to close appears and the test fails when clicking 'Yes Button'
				if (emulator.Destination.Z >= heightAtTargetLayer)
				{
					resetEvent.Set();
				}
			};

			resetEvent.WaitOne((int) (secondsToWait * 1000));
		}

		public static bool CompareExpectedSliceSettingValueWithActualVaue(string sliceSetting, string expectedValue)
		{
			foreach (string iniPath in Directory.GetFiles(ApplicationDataStorage.Instance.GCodeOutputPath, "*.ini"))
			{
				var settings = PrinterSettingsLayer.LoadFromIni(iniPath);

				if (settings.TryGetValue(sliceSetting, out string currentValue))
				{
					return currentValue.Trim() == expectedValue;
				}
			}

			return false;
		}

		public static void DeleteSelectedPrinter(AutomationRunner testRunner)
		{
			// Click 'Delete Printer' menu item
			testRunner.ClickByName("Printer Overflow Menu")
				.ClickByName("Delete Printer Menu Item")
				// Confirm Delete
				.WaitForName("HeaderRow");
			testRunner.ClickByName("Yes Button");
		}

		public static AutomationRunner AddAndSelectPrinter(this AutomationRunner testRunner, string make = "Airwolf 3D", string model = "HD")
		{
			testRunner.GetWidgetByName("PartPreviewContent", out SystemWindow systemWindow, 10);

			testRunner.WaitforDraw(systemWindow) // make sure we wait for MC to be up and running
				.EnsureWelcomePageClosed(); // close the welcome message

			if (testRunner.NamedWidgetExists("Cancel Wizard Button"))
			{
				testRunner.ClickByName("Cancel Wizard Button");
			}

			// Click 'Add Printer' if not on screen
			if (!testRunner.NameExists("AddPrinterWidget", 0.2))
			{
				if (!testRunner.NameExists("Create Printer", 0.2))
				{
					// go to the start page
					testRunner.ClickByName("Hardware Tab")
						.ClickByName("Create Printer");
				}
				else
				{
					if (testRunner.NameExists("Print Button", .2))
					{
						testRunner.ClickByName("Print Button");
					}
					else
					{
						testRunner.ClickByName("Create Printer");
					}
				}
			}

			// Wait for the tree to load before filtering
			testRunner.WaitFor(() =>
			{
				var widget = testRunner.GetWidgetByName("AddPrinterWidget", out _) as AddPrinterWidget;
				return widget.TreeLoaded;
			});

			// Apply filter
			testRunner.ClickByName("Search")
				.Type(model)
				.Type("{Enter}")
				.Delay()
				.ClickByName($"Node{make}{model}") // Click printer node
				.ClickByName("Next Button") // Continue to next page
				.Delay()
				.WaitFor(() => testRunner.ChildExists<SetupStepComPortOne>());
			testRunner.ClickByName("Cancel Wizard Button")
				.WaitFor(() => !testRunner.ChildExists<SetupStepComPortOne>());

			testRunner.VerifyAndRemovePhil();

			return testRunner;
		}

		public static AutomationRunner VerifyAndRemovePhil(this AutomationRunner testRunner)
        {
			var view3D = testRunner.GetWidgetByName("View3DWidget", out _, 3) as View3DWidget;
			var scene = view3D.Object3DControlLayer.Scene;

			testRunner.WaitFor(() => scene.Children.Count == 1);
			Assert.AreEqual(1, scene.Children.Count, "Should have a Phil on the bed");
			testRunner.WaitFor(() => scene.Children.First().Name == "Phil A Ment.stl");
			Assert.AreEqual("Phil A Ment.stl", scene.Children.First().Name);

			testRunner.Type("^a"); // clear the selection (type a space)
			testRunner.WaitFor(() => scene.SelectedItem != null);
			testRunner.Type("{BACKSPACE}");
			testRunner.WaitFor(() => scene.Children.Count == 0);

			return testRunner;
		}

		public static AutomationRunner CloneAndSelectPrinter(this AutomationRunner testRunner, string profileName)
		{
			testRunner.GetWidgetByName("PartPreviewContent", out SystemWindow systemWindow, 10);

			testRunner.WaitforDraw(systemWindow) // make sure we wait for MC to be up and running
				.EnsureWelcomePageClosed(); // close the welcome message

			if (testRunner.NamedWidgetExists("Cancel Wizard Button"))
			{
				testRunner.ClickByName("Cancel Wizard Button");
			}

			// go to the start page
			testRunner.ClickByName("Hardware Tab")
				.ClickByName("Import Printer Button");

			string profilePath = TestContext.CurrentContext.ResolveProjectPath(4, "Tests", "TestData", "TestProfiles", profileName + ".printer");

			// Apply filter
			testRunner.ClickByName("Profile Path Widget") // Continue to next page
				.Type(Path.GetFullPath(profilePath)) // open the right file
				.Type("{Tab}")
				.ClickByName("Import Button") // Continue to next page
				.ClickByName("Cancel Wizard Button")
				.DoubleClickByName(profileName + " Node")
				.WaitForName("PrintPopupMenu");
				
			return testRunner;
		}

		public static AutomationRunner EnsureWelcomePageClosed(this AutomationRunner testRunner)
		{
			testRunner.WaitFor(() => testRunner.NameExists("Cancel Wizard Button"));
			// Close the WelcomePage window if active
			if (testRunner.NameExists("Cancel Wizard Button", 1))
			{
				testRunner.ClickByName("Cancel Wizard Button");
			}

			testRunner.WaitFor(() => !testRunner.NameExists("Cancel Wizard Button", .1));

			return testRunner;
		}

		public static void WaitForAndCancelPrinterSetupPage(this AutomationRunner testRunner)
		{
			testRunner.WaitFor(() =>
			{
				return testRunner.GetWidgetByName("HeaderRow", out _) is GuiWidget headerRow
					&& headerRow.Parents<DialogPage>().FirstOrDefault() is SetupStepMakeModelName;
			});

			testRunner.ClickByName("Cancel Wizard Button");
		}

		public static AutomationRunner SwitchToHardwareTab(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Hardware Tab");
			return testRunner;
		}

		private static void OutputImage(ImageBuffer imageToOutput, string fileName)
		{
			if (saveImagesForDebug)
			{
				ImageTgaIO.Save(imageToOutput, fileName);
			}
		}

		private static void OutputImage(GuiWidget widgetToOutput, string fileName)
		{
			if (saveImagesForDebug)
			{
				OutputImage(widgetToOutput.BackBuffer, fileName);
			}
		}

		private static void OutputImages(GuiWidget control, GuiWidget test)
		{
			OutputImage(control, "image-control.tga");
			OutputImage(test, "image-test.tga");
		}

		/// <summary>
		/// Overrides the AppData location, ensuring each test starts with a fresh MatterControl database.
		/// </summary>
		public static void OverrideAppDataLocation(string matterControlDirectory)
		{
			string tempFolderPath = Path.Combine(matterControlDirectory, "Tests", "temp", runName, $"Test{testID++}");
			ApplicationDataStorage.Instance.OverrideAppDataLocation(tempFolderPath, () => DesktopSqlite.CreateInstance());
		}

		public static void AddItemsToQueue(string queueItemFolderToLoad)
		{
			// Default location of mcp file
			string mcpPath = Path.Combine(ApplicationDataStorage.ApplicationUserDataPath, "data", "default.mcp");

			Directory.CreateDirectory(Path.GetDirectoryName(mcpPath));

			if (!File.Exists(mcpPath))
			{
				File.WriteAllText(mcpPath, JsonConvert.SerializeObject(new LegacyQueueFiles()
				{
					ProjectFiles = new List<PrintItem>()
				}, Formatting.Indented));
			}

			var queueItemData = JsonConvert.DeserializeObject<LegacyQueueFiles>(File.ReadAllText(mcpPath));

			string queueData = Path.Combine(ApplicationDataStorage.ApplicationUserDataPath, "data", "testitems");

			// Create empty TestParts folder
			Directory.CreateDirectory(queueData);

			string queueItemsDirectory = TestContext.CurrentContext.ResolveProjectPath(5, "MatterControl", "Tests", "TestData", "QueueItems", queueItemFolderToLoad);

			foreach (string file in Directory.GetFiles(queueItemsDirectory))
			{
				string newFilePath = Path.Combine(queueData, Path.GetFileName(file));
				File.Copy(file, newFilePath, true);
				queueItemData.ProjectFiles.Add(new PrintItem()
				{
					FileLocation = newFilePath,
					Name = Path.GetFileNameWithoutExtension(file),
					DateAdded = DateTime.Now
				});
			}

			File.WriteAllText(mcpPath, JsonConvert.SerializeObject(queueItemData, Formatting.Indented));

			Assert.IsTrue(queueItemData != null && queueItemData.ProjectFiles.Count > 0);
		}

		public static AutomationRunner OpenUserPopupMenu(this AutomationRunner testRunner)
		{
			return testRunner.ClickByName("User Options Menu");
		}

		public static AutomationRunner ClickButton(this AutomationRunner testRunner, string buttonName, string buttonText, double maxWait = 5)
		{
			testRunner.WaitForName(buttonName, maxWait, predicate: (w) => w.Children.FirstOrDefault().Text == buttonText);
			return testRunner.ClickByName(buttonName);
		}

		public static AutomationRunner ClickResumeButton(this AutomationRunner testRunner,
			PrinterConfig printer,
			bool resume,
			int expectedLayer)
		{
			var buttonName = resume ? "Yes Button" : "No Button";
			var buttonText = resume ? "Resume" : "OK";
			testRunner.WaitForName(buttonName, 90, predicate: (w) => w.Children.FirstOrDefault().Text == buttonText);
			Assert.AreEqual(expectedLayer,
				printer.Connection.CurrentlyPrintingLayer,
				$"Expected the paused layer to be {expectedLayer} but was {printer.Connection.CurrentlyPrintingLayer}.");

			testRunner.ClickByName(buttonName)
				.WaitFor(() => !testRunner.NameExists(buttonName), 2);

			return testRunner;
		}

		public static AutomationRunner NavigateToFolder(this AutomationRunner testRunner, string libraryRowItemName)
		{
			testRunner.EnsureContentMenuOpen();

			if (!testRunner.NameExists(libraryRowItemName, .2))
			{
				// go back to the home section
				testRunner.ClickByName("Bread Crumb Button Home")
					.Delay();

				switch (libraryRowItemName)
				{
					case "SD Card Row Item Collection":
						if (ApplicationController.Instance.DragDropData.View3DWidget?.Printer is PrinterConfig printer)
						{
							testRunner.DoubleClickByName($"{printer.PrinterName} Row Item Collection")
								.Delay();
						}

						break;

					case "Calibration Parts Row Item Collection":
					case "Primitives Row Item Collection":
						// If visible, navigate into Libraries container before opening target
						testRunner.DoubleClickByName("Design Apps Row Item Collection")
							.Delay();
						break;

					case "Downloads Row Item Collection":
						testRunner.DoubleClickByName("Computer Row Item Collection")
							.Delay();
						break;

					case "Cloud Library Row Item Collection":
					case "Queue Row Item Collection":
					case "Local Library Row Item Collection":
						break;
				}
			}

			testRunner.DoubleClickByName(libraryRowItemName);
			return testRunner;
		}

		public static AutomationRunner EnsureContentMenuOpen(this AutomationRunner testRunner)
		{
			if (!testRunner.WaitForName("FolderBreadCrumbWidget", secondsToWait: 0.2))
			{
				testRunner.ClickByName("Add Content Menu")
					.Delay();
			}

			return testRunner;
		}

		public static void OpenRequiredSetupAndConfigureMaterial(this AutomationRunner testRunner)
		{
			// Complete new material selection requirement
			testRunner.ClickByName("PrintPopupMenu")
				.ClickByName("SetupPrinter")
				// Configure ABS as selected material
				// testRunner.ClickByName("Material DropDown List")
				// testRunner.ClickByName("ABS Menu")

				// Currently material selection is not required, simply act of clicking 'Select' clears setup required
				.ClickByName("Already Loaded Button");
		}

		public static AutomationRunner NavigateToLibraryHome(this AutomationRunner testRunner)
		{
			return testRunner.EnsureContentMenuOpen()
				.ClickByName("Bread Crumb Button Home")
				.Delay(.5);
		}

		public static void InvokeLibraryAddDialog(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Print Library Overflow Menu")
				.ClickByName("Add Menu Item");
		}

		public static AutomationRunner InvokeLibraryCreateFolderDialog(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Print Library Overflow Menu")
				.ClickByName("Create Folder... Menu Item");

			return testRunner;
		}

		public static string CreateChildFolder(this AutomationRunner testRunner, string folderName)
		{
			testRunner.InvokeLibraryCreateFolderDialog()
				.WaitForName("InputBoxPage Action Button");
			testRunner.Type(folderName)
				.ClickByName("InputBoxPage Action Button");

			string folderID = $"{folderName} Row Item Collection";

			Assert.IsTrue(testRunner.WaitForName(folderID), $"{folderName} exists");

			return folderID;
		}

		/// <summary>
		/// Types the specified text into the dialog and sends {Enter} to complete the interaction
		/// </summary>
		/// <param name="testRunner">The TestRunner to interact with</param>
		/// <param name="textValue">The text to type</param>
		public static void CompleteDialog(this AutomationRunner testRunner, string textValue, double secondsToWait = 2)
		{
			// AutomationDialog requires no delay
			if (AggContext.FileDialogs is AutomationDialogProvider)
			{
				// Wait for text widget to have focus
				var widget = testRunner.GetWidgetByName("Automation Dialog TextEdit", out _, 5);
				testRunner.WaitFor(() => widget.ContainsFocus);
			}
			else
			{
				testRunner.Delay(secondsToWait);
			}

			testRunner.Type(textValue)
				.Type("{Enter}")
				.WaitForWidgetDisappear("Automation Dialog TextEdit", 5);
		}

		public static AutomationRunner AddItemToBed(this AutomationRunner testRunner, string containerName = "Calibration Parts Row Item Collection", string partName = "Row Item Calibration - Box.stl")
		{
			if (!testRunner.NameExists(partName, 1) && !string.IsNullOrEmpty(containerName))
			{
				testRunner.NavigateToFolder(containerName);
			}

			var partWidget = testRunner.GetWidgetByName(partName, out _, onlyVisible: false) as ListViewItemBase;
			if (!partWidget.IsSelected)
			{
				testRunner.ScrollIntoView(partName);
				testRunner.ClickByName(partName);
			}

			testRunner.ClickByName("Print Library Overflow Menu");

			var view3D = testRunner.GetWidgetByName("View3DWidget", out _) as View3DWidget;
			var scene = view3D.Object3DControlLayer.Scene;
			var preAddCount = scene.Children.Count();

			testRunner.ClickByName("Add to Bed Menu Item")
				// wait for the object to be added
				.WaitFor(() => scene.Children.Count == preAddCount + 1);
			// wait for the object to be done loading
			var insertionGroup = scene.Children.LastOrDefault() as InsertionGroupObject3D;
			if (insertionGroup != null)
			{
				testRunner.WaitFor(() => scene.Children.LastOrDefault() as InsertionGroupObject3D != null, 10);
			}

			return testRunner;
		}

		public static AutomationRunner SaveBedplateToFolder(this AutomationRunner testRunner, string newFileName, string folderName)
		{
			return testRunner.ClickByName("Save Menu SplitButton", offset: new Point2D(30, 0))
				.ClickByName("Save As Menu Item")
				.Delay(1)
				.Type(newFileName)
				.NavigateToFolder(folderName)
				.ClickByName("Accept Button")
				// Give the SaveAs window time to close before returning to the caller
				.Delay(2);
		}

		public static AutomationRunner WaitForPrintFinished(this AutomationRunner testRunner, PrinterConfig printer, int maxSeconds = 500)
		{
			testRunner.WaitFor(() => printer.Connection.CommunicationState == CommunicationStates.FinishedPrint, maxSeconds);
			// click the ok button on the print complete dialog
			testRunner.ClickByName("Cancel Wizard Button");

			return testRunner;
		}

		/// <summary>
		/// Gets a reference to the first and only active printer. Throws if called when multiple active printers exists
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		/// <returns>The first active printer</returns>
		public static PrinterConfig FirstPrinter(this AutomationRunner testRunner)
		{
			Assert.AreEqual(1, ApplicationController.Instance.ActivePrinters.Count(), "FirstPrinter() is only valid in single printer scenarios");

			return ApplicationController.Instance.ActivePrinters.First();
		}

		public static AutomationRunner CloseFirstPrinterTab(this AutomationRunner testRunner)
		{
			// Close all printer tabs
			var mainViewWidget = testRunner.GetWidgetByName("PartPreviewContent", out _) as MainViewWidget;
			if (mainViewWidget.TabControl.AllTabs.First(t => t.TabContent is PrinterTabPage) is GuiWidget widget)
			{
				var closeWidget = widget.Descendants<ImageWidget>().First();
				Assert.AreEqual("Close Tab Button", closeWidget.Name, "Expected widget ('Close Tab Button') not found");

				testRunner.ClickWidget(closeWidget);

				// close the save dialog
				testRunner.ClickByName("No Button");
			}

			return testRunner;
		}

		public static void WaitForCommunicationStateDisconnected(this AutomationRunner testRunner, PrinterConfig printer, int maxSeconds = 500)
		{
			testRunner.WaitFor(() => printer.Connection.CommunicationState == CommunicationStates.Disconnected, maxSeconds);
		}

		public static async Task RunTest(
			AutomationTest testMethod,
			string staticDataPathOverride = null,
			double maxTimeToRun = 60,
			QueueTemplate queueItemFolderToAdd = QueueTemplate.None,
			int overrideWidth = -1,
			int overrideHeight = -1,
			string defaultTestImages = null)
		{
			// Walk back a step in the stack and output the callers name
			// StackTrace st = new StackTrace(false);
			// Debug.WriteLine("\r\n ***** Running automation test: {0} {1} ", st.GetFrames().Skip(1).First().GetMethod().Name, DateTime.Now);

			if (staticDataPathOverride == null)
			{
				// Popping one directory above MatterControl, then back down into MatterControl ensures this works in MCCentral as well and MatterControl
				staticDataPathOverride = TestContext.CurrentContext.ResolveProjectPath(5, "MatterControl", "StaticData");
			}

#if DEBUG
			string outputDirectory = "Debug";
#else
			string outputDirectory = "Release";
#endif

			Environment.CurrentDirectory = TestContext.CurrentContext.ResolveProjectPath(5, "MatterControl", "bin", outputDirectory);

			// Override the default SystemWindow type without config.json
			// AggContext.Config.ProviderTypes.SystemWindowProvider = "MatterHackers.Agg.UI.OpenGLWinformsWindowProvider, agg_platform_win32";
			AggContext.Config.ProviderTypes.SystemWindowProvider = "MatterHackers.MatterControl.WinformsSingleWindowProvider, MatterControl.Winforms";

#if !__ANDROID__
			// Set the static data to point to the directory of MatterControl
			StaticData.RootPath = staticDataPathOverride;
#endif
			// Popping one directory above MatterControl, then back down into MatterControl ensures this works in MCCentral as well and MatterControl
			MatterControlUtilities.OverrideAppDataLocation(TestContext.CurrentContext.ResolveProjectPath(5, "MatterControl"));

			if (queueItemFolderToAdd != QueueTemplate.None)
			{
				MatterControlUtilities.AddItemsToQueue(queueItemFolderToAdd.ToString());
			}

			if (defaultTestImages == null)
			{
				defaultTestImages = TestContext.CurrentContext.ResolveProjectPath(4, "Tests", "TestData", "TestImages");
			}

			UserSettings.Instance.set(UserSettingsKey.ThumbnailRenderingMode, "orthographic");
			// The EULA popup throws off the tests on Linux.
			UserSettings.Instance.set(UserSettingsKey.SoftwareLicenseAccepted, "true");
			// GL.HardwareAvailable = false;

			var config = TestAutomationConfig.Load();
			if (config.UseAutomationDialogs)
			{
				AggContext.Config.ProviderTypes.DialogProvider = "MatterHackers.Agg.Platform.AutomationDialogProvider, GuiAutomation";
			}

			// Extract mouse speed from config
			AutomationRunner.TimeToMoveMouse = config.TimeToMoveMouse;
			AutomationRunner.UpDelaySeconds = config.MouseUpDelay;

			// Automation runner must do as much as program.cs to spin up platform
			string platformFeaturesProvider = "MatterHackers.MatterControl.WindowsPlatformsFeatures, MatterControl.Winforms";
			AppContext.Platform = AggContext.CreateInstanceFrom<INativePlatformFeatures>(platformFeaturesProvider);
			AppContext.Platform.InitPluginFinder();
			AppContext.Platform.ProcessCommandline();

			var (width, height) = RootSystemWindow.GetStartupBounds();

			rootSystemWindow = Application.LoadRootWindow(
				overrideWidth == -1 ? width : overrideWidth,
				overrideHeight == -1 ? height : overrideHeight);

			OemSettings.Instance.ShowShopButton = false;

			if (!config.UseAutomationMouse)
			{
				AutomationRunner.InputMethod = new WindowsInputMethods();
			}

			await AutomationRunner.ShowWindowAndExecuteTests(
				rootSystemWindow,
				testMethod,
				maxTimeToRun,
				defaultTestImages,
				closeWindow: (testRunner) =>
				{
					foreach (var printer in ApplicationController.Instance.ActivePrinters)
					{
						if (printer.Connection.CommunicationState == CommunicationStates.Printing)
						{
							printer.Connection.Disable();
						}
					}

					rootSystemWindow.Close();

					testRunner.Delay();
					if (testRunner.NameExists("No Button"))
					{
						testRunner.ClickDiscardChanges();
					}
				});
		}

		public static void LibraryEditSelectedItem(AutomationRunner testRunner)
		{
			testRunner.ClickByName("Edit Menu Item");
			testRunner.Delay(1); // wait for the new window to open
		}

		public static void LibraryRenameSelectedItem(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Print Library Overflow Menu");
			testRunner.ClickByName("Rename Menu Item");
		}

		public static void LibraryRemoveSelectedItem(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Print Library Overflow Menu");
			testRunner.ClickByName("Remove Menu Item");
			testRunner.ClickByName("Yes Button");
		}

		public static void LibraryMoveSelectedItem(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Print Library Overflow Menu");
			testRunner.ClickByName("Move Menu Item");
		}

		public static string ResolveProjectPath(this TestContext context, int stepsToProjectRoot, params string[] relativePathSteps)
		{
			string assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

			var allPathSteps = new List<string> { assemblyPath };
			allPathSteps.AddRange(Enumerable.Repeat("..", stepsToProjectRoot));

			if (relativePathSteps.Any())
			{
				allPathSteps.AddRange(relativePathSteps);
			}

			return Path.GetFullPath(Path.Combine(allPathSteps.ToArray()));
		}

		/// <summary>
		/// Set the working directory to the location of the executing assembly. This is essentially the Nunit2 behavior
		/// </summary>
		/// <param name="context"></param>
		public static void SetCompatibleWorkingDirectory(this TestContext context)
		{
			Environment.CurrentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		}

		public static AutomationRunner StartSlicing(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Generate Gcode Button");
			return testRunner;
		}

		public static AutomationRunner OpenPrintPopupMenu(this AutomationRunner testRunner)
		{
			var printerConnection = ApplicationController.Instance.DragDropData.View3DWidget.Printer.Connection;

			if (printerConnection.CommunicationState != CommunicationStates.Connected
				&& printerConnection.CommunicationState != CommunicationStates.FinishedPrint)
			{
				testRunner.ClickByName("Connect to printer button");
				testRunner.WaitFor(() => printerConnection.CommunicationState == CommunicationStates.Connected);
			}

			// Open PopupMenu
			testRunner.ClickByName("PrintPopupMenu");

			// Wait for child control
			testRunner.WaitForName("Start Print Button");
			return testRunner;
		}

		public static AutomationRunner WaitForLayerAndResume(this AutomationRunner testRunner, PrinterConfig printer, int indexToWaitFor)
		{
			testRunner.WaitForName("Yes Button", 15);

			// Wait for layer
			testRunner.WaitFor(() => printer.Bed.ActiveLayerIndex + 1 == indexToWaitFor, 10, 500);
			Assert.AreEqual(indexToWaitFor, printer.Bed.ActiveLayerIndex + 1, "Active layer index does not match expected");

			testRunner.ClickByName("Yes Button");
			testRunner.Delay();
			return testRunner;
		}

		/// <summary>
		/// Open the Print popup menu and click the Start Print button
		/// </summary>
		/// <param name="testRunner">The AutomationRunner we are using.</param>
		/// <param name="printer">The printer to run the print on.</param>
		/// <param name="pauseAtLayers">The string to write into the pause field in the print menu.</param>
		/// <returns>The automation runner to allow fluid design</returns>
		public static AutomationRunner StartPrint(this AutomationRunner testRunner,
			PrinterConfig printer,
			string pauseAtLayers = null)
		{
			// Open popup
			testRunner.OpenPrintPopupMenu();

			if (pauseAtLayers != null)
			{
				testRunner.OpenPrintPopupAdvanced();

				testRunner.ClickByName("Layer(s) To Pause Field");
				testRunner.Type(pauseAtLayers);
			}

			if (testRunner.NameExists("SetupPrinter", .2))
			{
				if (printer.Settings.GetValue<bool>(SettingsKey.use_z_probe))
				{
					testRunner.ClickByName("SetupPrinter")
						.ClickByName("Already Loaded Button")
						.ClickByName("Cancel Wizard Button")
						.OpenPrintPopupMenu();
				}
				else
				{
					testRunner.ClickByName("SetupPrinter")
						.ClickByName("Already Loaded Button")
						.ClickByName("Cancel Wizard Button")
						.OpenPrintPopupMenu();
				}
			}

			testRunner.ClickByName("Start Print Button");

			return testRunner;
		}

		/// <summary>
		/// Open the Print popup menu and click the Start Print button
		/// </summary>
		/// <param name="testRunner">The AutomationRunner we are using.</param>
		/// <param name="printer">The printer to run the print on.</param>
		/// <param name="exportedGCode">The exported gcode is loaded and put into this variable.</param>
		/// <returns>The automation runner to allow fluid design</returns>
		public static AutomationRunner ExportPrintAndLoadGCode(this AutomationRunner testRunner,
			PrinterConfig printer,
			out string exportedGCode)
		{
			// Open popup
			testRunner.OpenPrintPopupMenu();

			if (testRunner.NameExists("SetupPrinter"))
			{
				testRunner.ClickByName("SetupPrinter")
					.ClickByName("Already Loaded Button")
					.ClickByName("Cancel Wizard Button")
					.OpenPrintPopupMenu();
			}

			testRunner.ClickByName("Export GCode Button");

			// wait for the export to finish
			throw new NotImplementedException();

			exportedGCode = "";
			return testRunner;
		}

		public static AutomationRunner WaitForPause(this AutomationRunner testRunner, PrinterConfig printer, int expectedLayer)
		{
			testRunner.WaitForName("Yes Button", 15, predicate: (w) => w.Children.FirstOrDefault().Text == "Resume");
			// validate the current layer
			if (expectedLayer != printer.Connection.CurrentlyPrintingLayer)
			{
				throw new Exception($"Expected the paused layer to be {expectedLayer} but was {printer.Connection.CurrentlyPrintingLayer}.");
			}

			return testRunner;
		}

		public static void OpenPrintPopupAdvanced(this AutomationRunner testRunner)
		{
			// Expand advanced panel if needed
			if (!testRunner.NameExists("Layer(s) To Pause Field", .2))
			{
				testRunner.ClickByName("Advanced Section");
			}

			// wait for child
			testRunner.WaitForName("Layer(s) To Pause Field");
		}

		public static void OpenGCode3DOverflowMenu(this AutomationRunner testRunner)
		{
			var button = testRunner.GetWidgetByName("Layers3D Button", out _) as ICheckbox;
			if (!button.Checked)
			{
				testRunner.ClickByName("Layers3D Button");
			}

			testRunner.ClickByName("View3D Overflow Menu");
		}

		/// <summary>
		/// Switch to the primary SliceSettings tab
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		public static AutomationRunner SwitchToSliceSettings(this AutomationRunner testRunner)
		{
			OpenSettingsSidebar(testRunner);

			testRunner.WaitForWidgetEnabled("Slice Settings Tab");

			testRunner.ClickByName("Slice Settings Tab");

			return testRunner;
		}

		public static AutomationRunner WaitForPageAndAdvance(this AutomationRunner testRunner, string headerText)
		{
			testRunner.WaitForPage(headerText)
				.ClickByName("Next Button");

			return testRunner;
		}

		public static AutomationRunner Complete9StepLeveling(this AutomationRunner testRunner, int numUpClicks = 1)
		{
			testRunner.Delay()
				.WaitForPageAndAdvance("Print Leveling Overview")
				.WaitForPageAndAdvance("Heating the printer");

			for (int i = 0; i < 3; i++)
			{
				var section = (i * 3) + 1;

				testRunner.WaitForPage($"Step {section} of 9");
				for (int j = 0; j < numUpClicks; j++)
				{
					testRunner.Delay();
					testRunner.ClickByName("Move Z positive");
				}

				testRunner.WaitForPage($"Step {section} of 9")
					.ClickByName("Next Button")
					.WaitForPage($"Step {section + 1} of 9")
					.ClickByName("Next Button")
					.WaitForPage($"Step {section + 2} of 9")
					.ClickByName("Next Button");
			}

			testRunner.ClickByName("Done Button")
				.Delay();

			if (testRunner.NameExists("Already Loaded Button", 0.2))
			{
				testRunner.ClickByName("Already Loaded Button");
			}

			// Close the staged wizard window
			testRunner.ClickByName("Cancel Wizard Button");

			return testRunner;
		}

		/// <summary>
		/// Switch to printer settings
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		public static AutomationRunner SwitchToPrinterSettings(this AutomationRunner testRunner, bool pinSettingsOpen = true)
		{
			testRunner.OpenSettingsSidebar(pinSettingsOpen);

			if (!testRunner.NameExists("Printer Tab", 0.1))
			{
				testRunner.ClickByName("Printer Overflow Menu")
					.ClickByName("Show Printer Menu Item");

				if (!pinSettingsOpen)
				{
					// close the menu
					testRunner.ClickByName("Printer Overflow Menu");
				}
			}

			if (pinSettingsOpen)
			{
				return testRunner.ClickByName("Printer Tab");
			}
			else
			{
				return testRunner.ClickByName("Printer Sidebar");
			}
		}

		public static void InlineTitleEdit(this AutomationRunner testRunner, string controlName, string replaceString)
		{
			testRunner.ClickByName(controlName + " Edit");
			testRunner.ClickByName(controlName + " Field");
			var textWidget = testRunner.GetWidgetByName(controlName + " Field", out _);
			textWidget.Text = replaceString;
			testRunner.ClickByName(controlName + " Save");
		}

		public static AutomationRunner NavigateToSliceSettingsField(this AutomationRunner testRunner, string slicerConfigName)
		{
			var settingData = PrinterSettings.SettingsData[slicerConfigName];

			var group = settingData.OrganizerGroup;

			var category = group.Category;

			// Click tab
			testRunner.ClickByName(category.Name + " Tab");

			// Open the subGroup if required
			var foundWidget = testRunner.GetWidgetByName(group.Name + " Panel", out _, .1);
			if (foundWidget == null)
			{
				// turn on advanced mode and try to get it again
				testRunner.ClickByName("Slice Settings Overflow Menu")
					.ClickByName("Advanced Menu Item");
				foundWidget = testRunner.GetWidgetByName(group.Name + " Panel", out _);
			}

			if (foundWidget != null)
			{
				var containerCheckBox = foundWidget.Descendants<ExpandCheckboxButton>().First();
				if (!containerCheckBox.Checked)
				{
					containerCheckBox.Checked = true;
					testRunner.Delay();
				}
			}

			return testRunner;
		}

		public static string SettingWidgetName(this string slicerConfigName)
		{
			var settingData = PrinterSettings.SettingsData[slicerConfigName];
			// Click field
			return $"{settingData.PresentationName} Field";
		}

		public static AutomationRunner SelectSliceSettingsField(this AutomationRunner testRunner, string slicerConfigName)
		{
			testRunner.NavigateToSliceSettingsField(slicerConfigName);

			// Click field
			var widgetName = SettingWidgetName(slicerConfigName);
			var foundWidget = testRunner.GetWidgetByName(widgetName, out _, .2, onlyVisible: false);
			if (foundWidget == null)
			{
				// turn on advanced mode and try to get it again
				testRunner.ClickByName("Slice Settings Overflow Menu")
					.ClickByName("Advanced Menu Item");

				foundWidget = testRunner.GetWidgetByName(widgetName, out _, 20, onlyVisible: false);
			}

			foreach (var scrollable in foundWidget.Parents<ScrollableWidget>())
			{
				scrollable.ScrollIntoView(foundWidget);
			}

			testRunner.ClickByName(widgetName);
			return testRunner;
		}

		/// <summary>
		/// Switch to Printer -> Controls
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		public static void SwitchToControlsTab(this AutomationRunner testRunner)
		{
			// Change to Printer Controls
			OpenSettingsSidebar(testRunner);

			if (!testRunner.NameExists("Controls Tab", 0.2))
			{
				testRunner.ClickByName("Printer Overflow Menu")
					.ClickByName("Show Controls Menu Item");
			}

			testRunner.ClickByName("Controls Tab");
		}

		/// <summary>
		/// Switch to Printer -> Terminal
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		public static void SwitchToTerminalTab(this AutomationRunner testRunner)
		{
			// Change to Printer Controls
			OpenSettingsSidebar(testRunner);

			if (!testRunner.NameExists("Terminal Tab", 0.2))
			{
				testRunner.ClickByName("Printer Overflow Menu")
					.ClickByName("Show Terminal Menu Item");
			}

			testRunner.ClickByName("Terminal Tab");
		}

		/// <summary>
		/// Switch to Printer -> GCode Tab - NOTE: as a short term hack this helper as adds content to the bed and slices to ensure GCode view options appear as expected
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		public static void SwitchToGCodeTab(this AutomationRunner testRunner)
		{
			testRunner.ClickByName("Layers3D Button");

			// TODO: Remove workaround needed to force GCode options to appear {{
			testRunner.AddItemToBed()
				.ClickByName("Generate Gcode Button");
			// TODO: Remove workaround needed to force GCode options to appear }}
		}

		public static void OpenSettingsSidebar(this AutomationRunner testRunner, bool pinOpen = true)
		{
			// If the sidebar exists, we need to expand and pin  it
			if (testRunner.NameExists("Slice Settings Sidebar", .1))
			{
				testRunner.ClickByName("Slice Settings Sidebar");
			}

			if (pinOpen
				&& UserSettings.Instance.get(UserSettingsKey.SliceSettingsTabPinned) != "true")
			{
				testRunner.ClickByName("Pin Settings Button");
			}
		}

		/// <summary>
		/// Adds the given asset names to the local library and validates the result
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		/// <param name="assetNames">The test assets to add to the library</param>
		public static AutomationRunner AddTestAssetsToLibrary(this AutomationRunner testRunner, IEnumerable<string> assetNames, string targetLibrary = "Local Library Row Item Collection")
		{
			// Switch to the Local Library tab
			testRunner.NavigateToFolder(targetLibrary);

			// Assert that the requested items are *not* in the list
			foreach (string assetName in assetNames)
			{
				string friendlyName = Path.GetFileNameWithoutExtension(assetName);
				Assert.IsFalse(testRunner.WaitForName($"Row Item {friendlyName}", .1), $"{friendlyName} part should not exist at test start");
			}

			// Add Library item
			testRunner.InvokeLibraryAddDialog();

			// Generate the full, quoted paths for the requested assets
			string fullQuotedAssetPaths = string.Join(" ", assetNames.Select(name => $"\"{MatterControlUtilities.GetTestItemPath(name)}\""));
			testRunner.CompleteDialog(fullQuotedAssetPaths);

			// Assert that the added items *are* in the list
			foreach (string assetName in assetNames)
			{
				string friendlyName = Path.GetFileNameWithoutExtension(assetName);
				string fileName = Path.GetFileName(assetName);

				// Look for either expected format (print queue differs from libraries)
				Assert.IsTrue(
					testRunner.WaitForName($"Row Item {friendlyName}", 2)
					|| testRunner.WaitForName($"Row Item {fileName}", 2),
					$"{friendlyName} part should exist after adding");
			}
			return testRunner;
		}

		/// <summary>
		/// Control clicks each specified item
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		/// <param name="widgetNames">The widgets to click</param>
		public static void SelectListItems(this AutomationRunner testRunner, params string[] widgetNames)
		{
			// Control click all items
			testRunner.PressModifierKeys(AutomationRunner.ModifierKeys.Control);
			foreach (var widgetName in widgetNames)
			{
				testRunner.ClickByName(widgetName);
			}

			testRunner.ReleaseModifierKeys(AutomationRunner.ModifierKeys.Control);
		}

		/// <summary>
		/// Uses the drag rectangle on the bed to select parts. Assumes the bed has been rotated to a
		/// bird's eye view (top down). That makes it easier to select the correct parts because the
		/// drag rectangle will be parallel to the XY plane.
		/// </summary>
		/// <param name="testRunner">The AutomationRunner in use</param>
		/// <param name="controlLayer">Object control layer from a View3DWidget</param>
		/// <param name="partNames">Names of the parts to select</param>
		/// <returns>The AutomationRunner</returns>
		public static AutomationRunner RectangleSelectParts(this AutomationRunner testRunner, Object3DControlsLayer controlLayer, IEnumerable<string> partNames)
		{
			var topWindow = controlLayer.Parents<SystemWindow>().First();
			var widgets = partNames
				.Select(name => ResolveName(controlLayer.Scene.Children, name))
				.Where(x => x.Ok)
				.Select(x =>
				{
					var widget = testRunner.GetWidgetByName(x.Name, out var containingWindow, 1);
					return new
					{
						Widget = widget ?? controlLayer,
						ContainingWindow = widget != null ? containingWindow : topWindow,
						x.Bounds
					};
				})
				.ToList();
			if (!widgets.Any())
			{
				return testRunner;
			}

			var minPosition = widgets.Aggregate((double.MaxValue, double.MaxValue), (acc, wi) =>
			{
				var bounds = wi.Widget.TransformToParentSpace(wi.ContainingWindow, wi.Bounds);
				var x = bounds.Left - 1;
				var y = bounds.Bottom - 1;
				return (x < acc.Item1 ? x : acc.Item1, y < acc.Item2 ? y : acc.Item2);
			});
			var maxPosition = widgets.Aggregate((0d, 0d), (acc, wi) =>
			{
				var bounds = wi.Widget.TransformToParentSpace(wi.ContainingWindow, wi.Bounds);
				var x = bounds.Right + 1;
				var y = bounds.Top + 1;
				return (x > acc.Item1 ? x : acc.Item1, y > acc.Item2 ? y : acc.Item2);
			});

			var systemWindow = widgets.First().ContainingWindow;
			testRunner.SetMouseCursorPosition(systemWindow, (int)minPosition.Item1, (int)minPosition.Item2);
			testRunner.DragToPosition(systemWindow, (int)maxPosition.Item1, (int)maxPosition.Item2).Drop();

			return testRunner;

			RectangleDouble GetBoundingBox(IObject3D part)
			{
				var screenBoundsOfObject3D = RectangleDouble.ZeroIntersection;
				var bounds = part.GetBVHData().GetAxisAlignedBoundingBox();

				for (var i = 0; i < 4; i += 1)
				{
					screenBoundsOfObject3D.ExpandToInclude(controlLayer.World.GetScreenPosition(bounds.GetTopCorner(i)));
					screenBoundsOfObject3D.ExpandToInclude(controlLayer.World.GetScreenPosition(bounds.GetBottomCorner(i)));
				}

				return screenBoundsOfObject3D;
			}

			(bool Ok, string Name, RectangleDouble Bounds)
				ResolveName(IEnumerable<IObject3D> parts, string name)
			{
				foreach (var part in parts)
				{
					if (part.Name == name)
					{
						return (true, name, GetBoundingBox(part));
					}

					if (part is GroupObject3D group)
					{
						var (ok, _, bounds) = ResolveName(group.Children, name);
						if (ok)
						{
							// WARNING the position of a part changes when it's added to a group.
							// Not sure if there's some sort of offset that needs to be applied or
							// if this is a bug. It is restored to its correct position when the
							// part is ungrouped.
							return (true, name, bounds);
						}
					}

					if (part is SelectionGroupObject3D selection)
					{
						var (ok, _, bounds) = ResolveName(selection.Children, name);
						if (ok)
						{
							return (true, name, bounds);
						}
					}
				}

				return (false, null, RectangleDouble.ZeroIntersection);
			}
		}
	}

	/// <summary>
	/// Represents a queue template folder on disk (located at Tests/TestData/QueueItems) that should be synced into the default
	/// queue during test init. The enum name and folder name *must* be the same in order to function
	/// </summary>
	public enum QueueTemplate
	{
		None,
		Three_Queue_Items,
		ReSliceParts
	}

	public class TestAutomationConfig
	{
		private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MHTest.config");

		/// <summary>
		/// The ClientToken used by tests to emulate an external client
		/// </summary>
		public string TestEnvClientToken { get; set; }

		/// <summary>
		/// The number of seconds to move the mouse when going to a new position.
		/// </summary>
		public double TimeToMoveMouse { get; set; } = .5;

		/// <summary>
		/// Determines if we use actual system file dialogs or simulated file dialogs.
		/// </summary>
		public bool UseAutomationDialogs { get; set; } = true;

		public bool UseAutomationMouse { get; set; } = true;

		public double MouseUpDelay { get; set; } = 0.2;

		/// <summary>
		/// The number of seconds the emulator should take to heat up and given target
		/// </summary>
		public double HeatupTime { get; set; } = 0.5;

		/// <summary>
		/// The number of seconds to wait after reaching the target temp before continuing. Analogous to
		/// firmware dwell time for temperature stabilization
		/// </summary>
		public double TempStabilizationTime { get; set; } = 0.5;

		public static TestAutomationConfig Load()
		{
			TestAutomationConfig config = null;

			if (!File.Exists(ConfigPath))
			{
				config = new TestAutomationConfig();
				config.Save();
			}
			else
			{
				config = JsonConvert.DeserializeObject<TestAutomationConfig>(File.ReadAllText(ConfigPath));
			}

			return config;
		}

		/// <summary>
		/// Persist the current settings to the 'MHTest.config' in the user profile - %userprofile%\MHTest.config
		/// </summary>
		public void Save()
		{
			File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
		}
	}
}
