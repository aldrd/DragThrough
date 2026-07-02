using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Chrome-style smooth drag reordering for the taskbar buttons.
    ///
    /// While a button is dragged it follows the cursor along the bar's axis in real time, and the
    /// other buttons slide out of the way with an eased animation as it crosses their midpoints.
    /// On release the dragged button animates into its final slot; only then is the new order
    /// committed to <see cref="TasksOrderManager"/>, so the commit is visually seamless (no snap).
    ///
    /// This replaces the GongSolutions drag-and-drop reordering that used a ghost adorner and an
    /// insertion caret. Layout is treated as a single strip along one axis (horizontal for a
    /// top/bottom taskbar, vertical for a left/right one); item sizes are measured individually,
    /// so buttons of differing sizes are handled correctly.
    /// </summary>
    public class TabStripDragHandler
    {
        private readonly ItemsControl _itemsControl;
        private readonly ICollectionView _view;
        private readonly TasksOrderManager _orderManager;

        // Eased slide of the buttons that make room for the dragged one.
        private const double ShiftMs = 180;
        // Final glide of the dragged button into its slot after the mouse is released.
        private const double SettleMs = 150;

        // A button's captured pre-drag geometry plus the transform used to animate it.
        private sealed class Slot
        {
            public FrameworkElement Container;
            public ApplicationWindow Data;
            public double Home;             // start position along the axis, relative to the ItemsControl
            public double Size;             // extent along the axis
            public TranslateTransform Transform;
            public double Target;           // current animated shift target (for the non-dragged buttons)
        }

        private bool _horizontal;

        // "Pending" = mouse is down on a button but the drag threshold has not been crossed yet, so a
        // plain click can still happen. "Dragging" = threshold crossed, the button is being dragged.
        private bool _pending;
        private bool _dragging;
        private Point _startPoint;
        private ApplicationWindow.WindowState _pressedState; // window state captured on press, for the synthesized click

        private ApplicationWindow _dragData;
        private readonly List<Slot> _slots = new();
        private Slot _dragSlot;
        private int _dragIndex;             // index of the dragged slot in home order
        private double _grabOffset;         // where inside the button the cursor grabbed it (along the axis)

        private int _insertPos;             // current insertion index among the non-dragged buttons
        private double _settleOffset;       // absolute axis position the dragged button glides to on release

        public TabStripDragHandler(ItemsControl itemsControl, ICollectionView view, TasksOrderManager orderManager)
        {
            _itemsControl = itemsControl;
            _view = view;
            _orderManager = orderManager;

            _itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            _itemsControl.PreviewMouseMove += OnPreviewMouseMove;
            _itemsControl.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            _itemsControl.LostMouseCapture += OnLostMouseCapture;
            _itemsControl.PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Reset();

            if (_itemsControl.ContainerFromElement((DependencyObject)e.OriginalSource) is not FrameworkElement container)
                return;

            if (container.DataContext is not ApplicationWindow data)
                return;

            _horizontal = Settings.Instance.Edge != (int)AppBarEdge.Left &&
                          Settings.Instance.Edge != (int)AppBarEdge.Right;
            _dragData = data;
            _pressedState = data.State;
            _startPoint = e.GetPosition(_itemsControl);
            _pending = true;

            // Capture the whole gesture on the strip up front and stop the event before the inner
            // Button can grab it: a Button that owns mouse capture would swallow the drag. We
            // reproduce the button's click ourselves on release when no drag happened (see
            // PerformClick), so ordinary clicks still activate/minimise the window.
            _itemsControl.CaptureMouse();
            e.Handled = true;
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                UpdateDrag(e.GetPosition(_itemsControl));
                return;
            }

            if (!_pending)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _itemsControl.ReleaseMouseCapture();
                Reset();
                return;
            }

            // With only one button there is nothing to reorder, so stay in the click-only state.
            if (_itemsControl.Items.Count < 2)
                return;

            Point p = e.GetPosition(_itemsControl);
            if (Math.Abs(p.X - _startPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            BeginDrag(p);
        }

        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                e.Handled = true;
                EndDrag(); // releases capture and glides the button into place
                return;
            }

            if (_pending)
            {
                e.Handled = true;
                ApplicationWindow clicked = _dragData;
                ApplicationWindow.WindowState state = _pressedState;
                _itemsControl.ReleaseMouseCapture();
                Reset();
                PerformClick(clicked, state);
            }
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            // Capture stolen from us mid-gesture (e.g. a context menu or focus change): finish cleanly.
            if (_dragging)
            {
                _dragging = false;
                Commit(Snapshot());
                Reset();
            }
            else if (_pending)
            {
                Reset();
            }
        }

        // Reproduces TaskButton's click behaviour, since capturing the gesture up front means the
        // inner Button never raises its own Click.
        private static void PerformClick(ApplicationWindow window, ApplicationWindow.WindowState pressedState)
        {
            if (window == null)
                return;

            if (pressedState == ApplicationWindow.WindowState.Active)
                window.Minimize();
            else
                window.BringToFront();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_dragging && e.Key == Key.Escape)
            {
                e.Handled = true;
                _dragging = false;
                _itemsControl.ReleaseMouseCapture();
                Session cancelled = Snapshot();
                cancelled.InsertPos = cancelled.DragIndex; // treat as no-op so the order is unchanged
                Commit(cancelled);
                Reset();
            }
        }

        private void BeginDrag(Point p)
        {
            // Keep the click-pending state until slots are built successfully, so a failed drag
            // start still falls back to a normal click on release rather than being swallowed.
            if (!BuildSlots())
                return;

            _pending = false;
            _grabOffset = Axis(_startPoint) - _dragSlot.Home;
            _dragging = true;

            Panel.SetZIndex(_dragSlot.Container, 1000);
            // The strip already holds mouse capture (taken on button-down), so the gesture continues
            // seamlessly into the drag.
            UpdateDrag(p);
        }

        // Snapshots every button's untransformed position and size along the axis.
        private bool BuildSlots()
        {
            _slots.Clear();
            _dragSlot = null;

            for (int i = 0; i < _itemsControl.Items.Count; i++)
            {
                if (_itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    return false;

                // Start from a clean transform so the measured position is the pure layout position.
                TranslateTransform transform = new();
                container.RenderTransform = transform;

                Point origin = container.TranslatePoint(new Point(0, 0), _itemsControl);
                var slot = new Slot
                {
                    Container = container,
                    Data = container.DataContext as ApplicationWindow,
                    Home = _horizontal ? origin.X : origin.Y,
                    Size = _horizontal ? container.ActualWidth : container.ActualHeight,
                    Transform = transform
                };

                _slots.Add(slot);
                if (ReferenceEquals(slot.Data, _dragData))
                    _dragSlot = slot;
            }

            if (_dragSlot == null || _slots.Count < 2)
                return false;

            _slots.Sort((a, b) => a.Home.CompareTo(b.Home));

            // Use the exact gap between neighbours as each button's pitch, so re-laying the strip out
            // sequentially reproduces the home positions precisely (margins and rounding included).
            // The last button keeps its measured size since it has no follower to measure against.
            for (int i = 0; i < _slots.Count - 1; i++)
                _slots[i].Size = _slots[i + 1].Home - _slots[i].Home;

            _dragIndex = _slots.IndexOf(_dragSlot);
            _insertPos = _dragIndex; // among the non-dragged buttons, its original spot
            return true;
        }

        private void UpdateDrag(Point p)
        {
            if (_dragSlot == null)
                return;

            double stripStart = _slots[0].Home;
            Slot last = _slots[_slots.Count - 1];
            double stripEnd = last.Home + last.Size;

            // The button tracks the cursor, clamped so it stays fully inside the strip.
            double desiredStart = Axis(p) - _grabOffset;
            desiredStart = Math.Max(stripStart, Math.Min(desiredStart, stripEnd - _dragSlot.Size));
            SetAxis(_dragSlot.Transform, desiredStart - _dragSlot.Home);

            double draggedCenter = desiredStart + _dragSlot.Size / 2;
            UpdateShifts(draggedCenter, stripStart);
        }

        // Decides where the dragged button would insert and slides the others to open that gap.
        private void UpdateShifts(double draggedCenter, double stripStart)
        {
            List<Slot> others = new(_slots.Count - 1);
            foreach (Slot s in _slots)
                if (!ReferenceEquals(s, _dragSlot))
                    others.Add(s);

            int insertPos = 0;
            foreach (Slot s in others)
            {
                if (s.Home + s.Size / 2 < draggedCenter)
                    insertPos++;
                else
                    break;
            }
            _insertPos = insertPos;

            // Lay the final order out sequentially to get each button's target position, which also
            // yields the slot the dragged button will settle into.
            double running = stripStart;
            for (int i = 0; i <= others.Count; i++)
            {
                if (i == insertPos)
                {
                    _settleOffset = running;
                    running += _dragSlot.Size;
                }

                if (i < others.Count)
                {
                    Slot s = others[i];
                    AnimateShift(s, running - s.Home);
                    running += s.Size;
                }
            }
        }

        private void AnimateShift(Slot slot, double target)
        {
            if (Math.Abs(slot.Target - target) < 0.5)
                return;

            slot.Target = target;
            DoubleAnimation animation = new(target, TimeSpan.FromMilliseconds(ShiftMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            slot.Transform.BeginAnimation(AxisProperty, animation);
        }

        private void EndDrag()
        {
            _dragging = false;
            _itemsControl.ReleaseMouseCapture();

            // Snapshot everything the commit needs so a new drag started during the settle
            // animation (which would Reset the fields) can't cancel this reorder.
            Session session = Snapshot();
            Reset();

            if (session.DragSlot == null)
            {
                Commit(session);
                return;
            }

            // Glide the dragged button into its slot, then commit once it lands.
            double target = session.SettleOffset - session.DragSlot.Home;
            DoubleAnimation animation = new(target, TimeSpan.FromMilliseconds(SettleMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) => Commit(session);
            session.DragSlot.Transform.BeginAnimation(AxisProperty, animation);
        }

        // Clears all transforms and, unless the button ended where it started, commits the new order.
        // Clearing the transforms and committing happen in the same synchronous pass, so no
        // intermediate frame is rendered: the last animation frame (old data order + transforms) and
        // the committed frame (new data order + no transforms) show the identical arrangement, so
        // the buttons never jump.
        private void Commit(Session session)
        {
            if (session.DragSlot != null && session.InsertPos != session.DragIndex)
            {
                List<Slot> others = new(session.Slots.Count - 1);
                foreach (Slot s in session.Slots)
                    if (!ReferenceEquals(s, session.DragSlot))
                        others.Add(s);

                ApplicationWindow after = session.InsertPos > 0 ? others[session.InsertPos - 1].Data : null;
                ApplicationWindow before = session.InsertPos < others.Count ? others[session.InsertPos].Data : null;

                ClearTransforms(session.Slots);
                _orderManager.Reorder(session.DragData, after?.Order ?? "", before?.Order ?? "");
                _view?.Refresh();
                return;
            }

            ClearTransforms(session.Slots);
        }

        private void ClearTransforms(List<Slot> slots)
        {
            foreach (Slot s in slots)
            {
                s.Transform.BeginAnimation(AxisProperty, null);
                SetAxis(s.Transform, 0);
                s.Container.RenderTransform = Transform.Identity;
                Panel.SetZIndex(s.Container, 0);
            }
        }

        // Immutable copy of the drag state handed to the (possibly deferred) commit.
        private sealed class Session
        {
            public List<Slot> Slots;
            public Slot DragSlot;
            public ApplicationWindow DragData;
            public int InsertPos;
            public int DragIndex;
            public double SettleOffset;
        }

        private Session Snapshot() => new()
        {
            Slots = new List<Slot>(_slots),
            DragSlot = _dragSlot,
            DragData = _dragData,
            InsertPos = _insertPos,
            DragIndex = _dragIndex,
            SettleOffset = _settleOffset
        };

        private void Reset()
        {
            _pending = false;
            _dragging = false;
            _dragData = null;
            _dragSlot = null;
            _slots.Clear();
        }

        private double Axis(Point p) => _horizontal ? p.X : p.Y;

        private DependencyProperty AxisProperty => _horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;

        private void SetAxis(TranslateTransform transform, double value)
        {
            if (_horizontal)
                transform.X = value;
            else
                transform.Y = value;
        }
    }
}
