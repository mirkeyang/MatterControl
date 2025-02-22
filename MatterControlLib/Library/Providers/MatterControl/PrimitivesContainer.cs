﻿/*
Copyright (c) 2019, John Lewin
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
using MatterHackers.Agg.Platform;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;

namespace MatterHackers.MatterControl.Library
{
	public class PrimitivesContainer : LibraryContainer
	{
		public PrimitivesContainer()
		{
			Name = "Primitives".Localize();
			DefaultSort = new LibrarySortBehavior()
			{
				SortKey = SortKey.ModifiedDate,
				Ascending = true,
			};
		}

		public override void Load()
		{
			var library = ApplicationController.Instance.Library;

			long index = DateTime.Now.Ticks;
			var libraryItems = new List<GeneratorItem>()
			{
				new GeneratorItem(
					"Cube".Localize(),
					async () => await CubeObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Pyramid".Localize(),
					async () => await PyramidObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Wedge".Localize(),
					async () => await WedgeObject3D_2.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Half Wedge".Localize(),
					async () => await HalfWedgeObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Text".Localize(),
					async () => await TextObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Cylinder".Localize(),
					async () => await CylinderObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Cone".Localize(),
					async () => await ConeObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Half Cylinder".Localize(),
					async () => await HalfCylinderObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Torus".Localize(),
					async () => await TorusObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Ring".Localize(),
					async () => await RingObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Sphere".Localize(),
					async () => await SphereObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Half Sphere".Localize(),
					async () => await HalfSphereObject3D.Create())
					{ DateCreated = new DateTime(index++) },
#if DEBUG
				new GeneratorItem(
					"SCAD Script".Localize(),
					async () => await OpenScadScriptObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Dual Contouring".Localize(),
					async () => await DualContouringObject3D.Create())
					{ DateCreated = new DateTime(index++) },
#endif
				new GeneratorItem(
					"Image Converter".Localize(),
					() =>
					{
						// Construct an image
						var imageObject = new ImageObject3D()
						{
							AssetPath = StaticData.Instance.ToAssetPath(Path.Combine("Images", "mh-logo.png"))
						};

						// Construct a scene
						var bedConfig = new BedConfig(null);
						var tempScene = bedConfig.Scene;
						tempScene.Children.Add(imageObject);
						tempScene.SelectedItem = imageObject;

						// Invoke ImageConverter operation, passing image and scene
						SceneOperations.ById("ImageConverter").Action(bedConfig);

						// Return replacement object constructed in ImageConverter operation
						var constructedComponent = tempScene.Children.LastOrDefault();
						tempScene.SelectedItem = constructedComponent;
						tempScene.Children.Remove(constructedComponent);

						return Task.FromResult(constructedComponent);
					})
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Measure Tool".Localize(),
					async () => await MeasureToolObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Description".Localize(),
					async () => await DescriptionObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Variable Sheet".Localize(),
					async () => await SheetObject3D.Create())
					{ DateCreated = new DateTime(index++) },
			};

			string title = "Primitive Shapes".Localize();

			foreach (var item in libraryItems)
			{
				item.Category = title;
				Items.Add(item);
			}

#if DEBUG
			this.ChildContainers.Add(
				new DynamicContainerLink(
					"Primitives 2D".Localize(),
					StaticData.Instance.LoadIcon(Path.Combine("Library", "folder.png")),
					StaticData.Instance.LoadIcon(Path.Combine("Library", "primitives_library_icon.png")),
					() => new Primitives2DContainer())
				{
					IsReadOnly = true
				});
#endif

		}
	}

	public class Primitives2DContainer : LibraryContainer
	{
		public Primitives2DContainer()
		{
			Name = "Primitives 2D".Localize();
			DefaultSort = new LibrarySortBehavior()
			{
				SortKey = SortKey.ModifiedDate,
				Ascending = true,
			};
		}

		public override void Load()
		{
			var library = ApplicationController.Instance.Library;

			long index = DateTime.Now.Ticks;
			var libraryItems = new List<GeneratorItem>()
			{
				new GeneratorItem(
					"Box".Localize(),
					async () => await BoxPathObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Triangle".Localize(),
					async () => await PyramidObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Trapezoid".Localize(),
					async () => await WedgeObject3D_2.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Text".Localize(),
					async () => await TextPathObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Oval".Localize(),
					async () => await CylinderObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Star".Localize(),
					async () => await ConeObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Ring".Localize(),
					async () => await RingObject3D.Create())
					{ DateCreated = new DateTime(index++) },
				new GeneratorItem(
					"Circle".Localize(),
					async () => await SphereObject3D.Create())
					{ DateCreated = new DateTime(index++) },
			};

			string title = "2D Shapes".Localize();

			foreach (var item in libraryItems)
			{
				item.Category = title;
				Items.Add(item);
			}
		}
	}
}
