﻿/*
Copyright (c) 2018, John Lewin
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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.PrintQueue;

namespace MatterHackers.MatterControl.Library
{
	public interface ILibraryContext
	{
		event EventHandler<ContainerChangedEventArgs> ContainerChanged;

		event EventHandler<ContainerChangedEventArgs> ContentChanged;

		ILibraryContainer ActiveContainer { get; set; }
	}

	public class ContainerChangedEventArgs : EventArgs
	{
		public ContainerChangedEventArgs(ILibraryContainer activeContainer, ILibraryContainer previousContainer)
		{
			this.ActiveContainer = activeContainer;
			this.PreviousContainer = previousContainer;
		}

		public ILibraryContainer ActiveContainer { get; }

		public ILibraryContainer PreviousContainer { get; }
	}

	public class LibraryConfig : ILibraryContext
	{
		public Dictionary<string, IContentProvider> ContentProviders = new Dictionary<string, IContentProvider>();

		private static ImageBuffer defaultFolderIcon = StaticData.Instance.LoadIcon(Path.Combine("Library", "folder.png")).SetPreMultiply();

		public static ImageBuffer DefaultItemIcon { get; } = StaticData.Instance.LoadIcon(Path.Combine("Library", "file.png"));

		private ILibraryContainer activeContainer;

		private SafeList<ILibraryContainerLink> libraryProviders;

		public LibraryConfig()
		{
			libraryProviders = new SafeList<ILibraryContainerLink>();

			this.RootLibaryContainer = new RootLibraryContainer(libraryProviders);

			this.ActiveContainer = this.RootLibaryContainer;
		}

		public event EventHandler<ContainerChangedEventArgs> ContainerChanged;

		public event EventHandler<ContainerChangedEventArgs> ContentChanged;

		public ILibraryContainer ActiveContainer
		{
			get => activeContainer;

			set
			{
				if (activeContainer == value)
				{
					return;
				}

				var newContainer = value;

				var eventArgs = new ContainerChangedEventArgs(newContainer, activeContainer);

				if (activeContainer != null)
				{
					activeContainer.Deactivate();
					activeContainer.ContentChanged -= ActiveContainer_ContentChanged;

					if (activeContainer.CustomSearch is ICustomSearch customSearch)
					{
						customSearch.ClearFilter();
					}

					// If the new container is an ancestor of the active container we need to Dispose everyone up to that point
					if (activeContainer.Ancestors().Where(p => p == newContainer).Any())
					{
						var context = activeContainer;
						while (context != newContainer)
						{
							context.Dispose();
							context = context.Parent;
						}
					}
				}

				activeContainer = newContainer;
				activeContainer.Activate();
				activeContainer.ContentChanged += ActiveContainer_ContentChanged;

				ContainerChanged?.Invoke(this, eventArgs);
			}
		}

		public DesignAppsCollectionContainer DesignAppsCollectionContainer { get; internal set; }

		public SafeList<LibraryAction> MenuExtensions { get; } = new SafeList<LibraryAction>();

		public PlatingHistoryContainer PlatingHistory { get; internal set; }

		public ILibraryContainer RootLibaryContainer { get; }

        public ImageBuffer EnsureCorrectThumbnailSizing(ImageBuffer originalThumbnail, int thumbWidth, int thumbHeight, Action<ImageBuffer> resizedImage)
		{
			var processingImage = originalThumbnail;
			// Resize canvas to target as fallback
			if (processingImage.Width < thumbWidth || processingImage.Height < thumbHeight)
			{
				processingImage = LibraryListView.ResizeCanvas(processingImage, thumbWidth, thumbHeight);
			}
			else if (processingImage.Width > thumbWidth || processingImage.Height > thumbHeight)
			{
				processingImage = LibraryProviderHelpers.ResizeImage(processingImage, thumbWidth, thumbHeight);
			}

			if (GuiWidget.DeviceScale != 1
				&& processingImage.Width != thumbWidth * GuiWidget.DeviceScale)
			{
				processingImage = processingImage.CreateScaledImage(GuiWidget.DeviceScale);
			}

			originalThumbnail.ImageChanged += (s, e) =>
			{
				// this happens when we get an updated image from a web request
				UiThread.RunOnIdle(() =>
				{
					resizedImage?.Invoke(originalThumbnail.CreateScaledImage(GuiWidget.DeviceScale));
				});
			};

			return processingImage;
		}

		public IContentProvider GetContentProvider(ILibraryItem item)
		{
			string contentType = (item as ILibraryAssetStream)?.ContentType ?? (item as ILibraryObject3D)?.ContentType;
			if (contentType == null)
			{
				return null;
			}

			return GetContentProvider(contentType);
		}

		public IContentProvider GetContentProvider(string contentType)
		{
			IContentProvider provider;
			ContentProviders.TryGetValue(contentType, out provider);

			return provider;
		}

		public bool IsContentFileType(string fileName)
		{
			string fileExtensionLower = Path.GetExtension(fileName).ToLower().Trim('.');

			return !string.IsNullOrEmpty(fileExtensionLower)
				&& (ApplicationSettings.LibraryFilterFileExtensions.Contains(fileExtensionLower)
					|| ApplicationController.Instance.Library.ContentProviders.Keys.Contains(fileExtensionLower));
		}

		public bool IsMeshFileType(string fileName)
		{
			string fileExtensionLower = Path.GetExtension(fileName).ToLower().Trim('.');
			return !string.IsNullOrEmpty(fileExtensionLower)
				&& ApplicationSettings.ValidFileExtensions.Contains(fileExtensionLower);
		}

		public async Task LoadItemThumbnail(Action<ImageBuffer> thumbnailListener, Action<MeshContentProvider> buildThumbnail, ILibraryItem libraryItem, ILibraryContainer libraryContainer, int thumbWidth, int thumbHeight, ThemeConfig theme)
		{
			async void setItemThumbnail(ImageBuffer icon)
			{
				if (icon != null)
				{
					if (icon.Width == 0)
					{
						return;
					}

					icon = await Task.Run(() => this.EnsureCorrectThumbnailSizing(icon, thumbWidth, thumbHeight, (image) =>
					{
						setItemThumbnail(image);
					}));
					thumbnailListener?.Invoke(icon);
				}
			}

			// Load from cache via LibraryID
			var thumbnail = await Task.Run(() => ApplicationController.Instance.Thumbnails.LoadCachedImage(libraryItem, thumbWidth, thumbHeight));
			if (thumbnail != null)
			{
				setItemThumbnail(thumbnail);
				return;
			}

			if (thumbnail == null && libraryContainer != null)
			{
				try
				{
					// Ask the container - allows the container to provide its own interpretation of the item thumbnail
					thumbnail = await libraryContainer.GetThumbnail(libraryItem, thumbWidth, thumbHeight);
				}
				catch
				{
				}
			}

			if (thumbnail == null && libraryItem is IThumbnail)
			{
				// If the item provides its own thumbnail, try to collect it
				thumbnail = await (libraryItem as IThumbnail).GetThumbnail(thumbWidth, thumbHeight);
			}

			if (thumbnail == null)
			{
				// Ask content provider - allows type specific thumbnail creation
				var contentProvider = ApplicationController.Instance.Library.GetContentProvider(libraryItem);
				if (contentProvider != null)
				{
					// Before we have a thumbnail set to the content specific thumbnail
					thumbnail = contentProvider.DefaultImage;

					if (contentProvider is MeshContentProvider meshContentProvider)
					{
						buildThumbnail?.Invoke(meshContentProvider);
					}
					else
					{
						// Show processing image
						setItemThumbnail(theme.GeneratingThumbnailIcon);

						// Ask the provider for a content specific thumbnail
						thumbnail = await contentProvider.GetThumbnail(libraryItem, thumbWidth, thumbHeight);
					}
				}
			}

			if (thumbnail == null)
			{
				// Use the listview defaults
				thumbnail = ((libraryItem is ILibraryContainerLink) ? defaultFolderIcon : DefaultItemIcon).AlphaToPrimaryAccent();
			}

			// TODO: Resolve and implement
			// Allow the container to draw an overlay - use signal interface or add method to interface?
			// var iconWithOverlay = ActiveContainer.DrawOverlay()

			setItemThumbnail(thumbnail);
		}

		public void RegisterContainer(ILibraryContainerLink containerItem)
		{
			libraryProviders.Add(containerItem);
			libraryProviders.Modify((list) =>
			{
				list.Sort(SortOnName);
			});
		}

		public void RegisterCreator(ILibraryObject3D libraryItem)
		{
			this.RootLibaryContainer.Items.Add(libraryItem);
		}

		public void RegisterCreator(ILibraryAssetStream libraryItem)
		{
			this.RootLibaryContainer.Items.Add(libraryItem);
		}

		/// <summary>
		/// Notifies listeners that the ActiveContainer Changed
		/// </summary>
		internal void NotifyContainerChanged()
		{
			this.OnContainerChanged(this.ActiveContainer);
		}

		private void ActiveContainer_ContentChanged(object sender, EventArgs args)
		{
			this.OnContainerChanged(this.ActiveContainer);
		}

		private void OnContainerChanged(ILibraryContainer container)
		{
			ContentChanged?.Invoke(this, new ContainerChangedEventArgs(container, null));
		}

		private int SortOnName(ILibraryContainerLink x, ILibraryContainerLink y)
		{
			if (x != null && x.Name != null
				&& y != null && y.Name != null)
			{
				return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
			}

			return 0;
		}
	}
}