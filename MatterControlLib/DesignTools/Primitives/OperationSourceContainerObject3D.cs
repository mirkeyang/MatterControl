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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.DataConverters3D.UndoCommands;
using MatterHackers.Localizations;
using MatterHackers.PolygonMesh;
using Newtonsoft.Json;

namespace MatterHackers.MatterControl.DesignTools.Operations
{
	public class OperationSourceContainerObject3D : Object3D
	{
		public static Func<string, Func<IProgress<ProgressStatus>, CancellationTokenSource, Task>, Task> TaskBuilder { get; set; } =
			(name, func) => Task.Run(() => func(null, null));

		public override Mesh Mesh
		{
			get
			{
				if (base.Mesh == null && !RebuildLocked)
				{
					int a = 0;
				}

				return base.Mesh;
			}

			set => base.Mesh = value;
		}

		public override bool CanApply => true;

		public override bool Persistable
		{
			get
			{
				if (SourceContainer != null)
				{
					if (SourceContainer.Children.Any(c => !c.Persistable))
					{
						return false;
					}
				}

				return true;
			}
		}

		[JsonIgnore]
		public IObject3D SourceContainer
		{
			get
			{
				IObject3D sourceContainer = this.Children.FirstOrDefault(c => c is OperationSourceObject3D);
				if (sourceContainer == null)
				{
					using (this.RebuildLock())
					{
						sourceContainer = new OperationSourceObject3D();

						// Move all the children to sourceContainer
						this.Children.Modify(thisChildren =>
						{
							sourceContainer.Children.Modify(sourceChildren =>
							{
								foreach (var child in thisChildren)
								{
									sourceChildren.Add(child);
								}
							});

							// and then add the source container to this
							thisChildren.Clear();
							thisChildren.Add(sourceContainer);
						});
					}
				}

				return sourceContainer;
			}
		}

		public override void Apply(UndoBuffer undoBuffer)
		{
			Apply(undoBuffer, null);
		}

		protected void Apply(UndoBuffer undoBuffer, IEnumerable<IObject3D> extraItems)
		{
			using (RebuildLock())
			{
				var newChildren = new List<IObject3D>();
				// push our matrix into a copy of our children
				foreach (var child in this.Children)
				{
					if (!(child is OperationSourceObject3D))
					{
						var newChild = child.Clone();
						newChildren.Add(newChild);
						newChild.Matrix *= this.Matrix;
						var flags = Object3DPropertyFlags.Visible;
						if (this.Color.alpha != 0) flags |= Object3DPropertyFlags.Color;
						if (this.OutputType != PrintOutputTypes.Default) flags |= Object3DPropertyFlags.OutputType;
						if (this.MaterialIndex != -1) flags |= Object3DPropertyFlags.MaterialIndex;
						newChild.CopyProperties(this, flags);
					}
				}

				if (newChildren.Count > 1)
				{
					// wrap the children in an object so they remain a group
					var group = new Object3D();
					group.Children.Modify((groupList) =>
					{
						groupList.AddRange(newChildren);
					});

					newChildren.Clear();
					newChildren.Add(group);
				}

				if (newChildren.Count == 0)
				{
					newChildren = this.Children.Select(i => i.Clone()).ToList();
				}

				// add flatten to the name to show what happened
				newChildren[0].Name = this.Name;

				// and replace us with the children
				var replaceCommand = new ReplaceCommand(new[] { this }, newChildren);

				if (undoBuffer != null)
				{
					undoBuffer.AddAndDo(replaceCommand);
				}
				else
				{
					replaceCommand.Do();
				}

				foreach (var child in newChildren[0].DescendantsAndSelf())
				{
					child.MakeNameNonColliding();
				}
			}

			Invalidate(InvalidateType.Children);
		}

		public override async void OnInvalidate(InvalidateArgs invalidateArgs)
		{
			// TODO: color and output type could have special consideration that would not require a rebuild
			// They could just propagate the color and output type to the correctly child and everything would be good
			if ((invalidateArgs.InvalidateType.HasFlag(InvalidateType.Children)
				|| invalidateArgs.InvalidateType.HasFlag(InvalidateType.Matrix)
				|| invalidateArgs.InvalidateType.HasFlag(InvalidateType.Mesh)
				|| invalidateArgs.InvalidateType.HasFlag(InvalidateType.Path)
				|| invalidateArgs.InvalidateType.HasFlag(InvalidateType.Color)
				|| invalidateArgs.InvalidateType.HasFlag(InvalidateType.OutputType))
				&& invalidateArgs.Source != this
				&& !RebuildLocked)
			{
				await Rebuild();
			}
			else if ((invalidateArgs.InvalidateType.HasFlag(InvalidateType.Properties) && invalidateArgs.Source == this))
			{
				await Rebuild();
			}
			else if (SheetObject3D.NeedsRebuild(this, invalidateArgs))
			{
				await Rebuild();
			}
			else if (invalidateArgs.InvalidateType.HasFlag(InvalidateType.Name)
				&& !NameOverriden)
			{
				Name = NameFromChildren();
				NameOverriden = false;
				base.OnInvalidate(invalidateArgs);
			}
			else
			{
				base.OnInvalidate(invalidateArgs);
			}
		}

		public virtual string NameFromChildren()
        {
			return CalculateName(SourceContainer.Children, ", ");
		}

		public override void Cancel(UndoBuffer undoBuffer)
		{
			using (RebuildLock())
			{
				var newChildren = new List<IObject3D>();
				// push our matrix into a copy of our children
				foreach (var child in this.SourceContainer.Children)
				{
					var newChild = child.Clone();
					newChildren.Add(newChild);
					newChild.Matrix *= this.Matrix;
					var flags = Object3DPropertyFlags.Visible;
					if (this.Color.alpha != 0) flags |= Object3DPropertyFlags.Color;
					if (this.OutputType != PrintOutputTypes.Default) flags |= Object3DPropertyFlags.OutputType;
					if (this.MaterialIndex != -1) flags |= Object3DPropertyFlags.MaterialIndex;
					newChild.CopyProperties(this, flags);
				}

				// and replace us with the children
				var replaceCommand = new ReplaceCommand(new[] { this }, newChildren, false);
				if (undoBuffer != null)
				{
					undoBuffer.AddAndDo(replaceCommand);
				}
				else
				{
					replaceCommand.Do();
				}
			}

			Invalidate(InvalidateType.Children);
		}

		public void RemoveAllButSource()
		{
			var sourceContainer = SourceContainer;
			this.Children.Modify(list =>
			{
				list.Clear();
				list.Add(sourceContainer);
			});
		}

		public virtual async void WrapSelectedItemAndSelect(InteractiveScene scene)
		{
			using (RebuildLock())
			{
				var selectedItems = scene.GetSelectedItems();

				if (selectedItems.Count > 0)
				{
					// clear the selected item
					scene.SelectedItem = null;

					using (RebuildLock())
					{
						var clonedItemsToAdd = new List<IObject3D>(selectedItems.Select((i) => i.Clone()));

						Children.Modify((list) =>
						{
							list.Clear();

							foreach (var child in clonedItemsToAdd)
							{
								list.Add(child);
							}
						});
					}

					scene.UndoBuffer.AddAndDo(
						new ReplaceCommand(
							new List<IObject3D>(selectedItems),
							new List<IObject3D> { this }));

					await this.Rebuild();

					Name = NameFromChildren();
					NameOverriden = false;
				}
			}

			// and select this
			var rootItem = this.Parents().Where(i => scene.Children.Contains(i)).FirstOrDefault();
			if (rootItem != null)
			{
				scene.SelectedItem = rootItem;
			}

			scene.SelectedItem = this;

			this.Invalidate(InvalidateType.Children);
		}
	}

	public class OperationSourceObject3D : Object3D
	{
		public override bool CanApply => true;

		public OperationSourceObject3D()
		{
			Name = "Source".Localize();
		}
	}
}