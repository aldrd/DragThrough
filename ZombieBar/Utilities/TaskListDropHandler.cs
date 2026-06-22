using System.ComponentModel;
using System.Linq;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using ManagedShell.WindowsTasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Handles reordering of taskbar buttons via drag and drop. Instead of moving items inside the
    /// bound collection, it computes a new order identifier for the dragged window from its new
    /// neighbors and lets <see cref="TasksOrderManager"/> persist it; the sorted view repositions
    /// the window automatically.
    /// </summary>
    public class TaskListDropHandler : IDropTarget
    {
        private readonly TasksOrderManager _orderManager;
        private readonly ICollectionView _view;

        public TaskListDropHandler(TasksOrderManager orderManager, ICollectionView view)
        {
            _orderManager = orderManager;
            _view = view;
        }

        public void DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is ApplicationWindow && dropInfo.TargetCollection != null)
            {
                dropInfo.Effects = DragDropEffects.Move;
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
            }
        }

        public void Drop(IDropInfo dropInfo)
        {
            if (dropInfo.Data is not ApplicationWindow dragged || dropInfo.TargetCollection == null)
                return;

            // The target collection is the sorted, filtered view, so it enumerates in displayed order.
            var visible = dropInfo.TargetCollection.OfType<ApplicationWindow>().ToList();
            int insertIndex = dropInfo.InsertIndex;

            // Find the visible neighbors around the insertion point, skipping the dragged item itself.
            ApplicationWindow afterItem = null;
            ApplicationWindow beforeItem = null;

            for (int i = insertIndex - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(visible[i], dragged))
                {
                    afterItem = visible[i];
                    break;
                }
            }

            for (int i = insertIndex; i < visible.Count; i++)
            {
                if (!ReferenceEquals(visible[i], dragged))
                {
                    beforeItem = visible[i];
                    break;
                }
            }

            _orderManager.Reorder(dragged, afterItem?.Order ?? "", beforeItem?.Order ?? "");

            // Re-sort the view so the dragged button moves to its new position immediately.
            _view?.Refresh();
        }
    }
}
