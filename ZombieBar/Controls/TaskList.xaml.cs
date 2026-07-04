using ManagedShell.AppBar;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ZombieBar.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZombieBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskList.xaml
    /// </summary>
    public partial class TaskList : UserControl
    {
        private bool isLoaded;
        private double DefaultButtonWidth;
        private double MinButtonWidth;
        private double TaskButtonLeftMargin;
        private double TaskButtonRightMargin;
        private ListCollectionView _windowsView;
        private TabStripDragHandler _dragHandler;
        private VirtualDesktopService _virtualDesktopService;

        // Handles of windows on the current virtual desktop, resolved via the virtual-desktop COM API
        // off the UI thread (COM STA calls pump messages and would re-enter ListCollectionView's live
        // shaping if done inside the filter). The filter only does a set lookup here.
        // Null = not yet computed / API unavailable -> show everything (fail open).
        private HashSet<IntPtr> _currentDesktopWindows;
        private bool _desktopRefreshScheduled;
        private bool _desktopRefreshDirty;

        // A task refresh/relayout was requested while a drag was in progress; running it then would
        // regenerate the item containers and abort the drag, so it is deferred until the drag completes.
        private bool _refreshPendingAfterDrag;

        // The COM "is on current desktop" result can be momentarily unsettled right after a switch, so
        // we recompute once more a short while later.
        private DispatcherTimer _desktopSettleTimer;

        // The WrapPanel that hosts the task buttons; it is inset from the left to center the buttons when
        // the bar isn't full and the "center tasks" option is on. Cached lazily (exists once items realize).
        private WrapPanel _itemsPanel;

        // Guard for the LayoutUpdated handler so it only rebalances when the visible count or the
        // available width actually changed (LayoutUpdated otherwise fires on every layout pass).
        private int _lastLayoutCount = -1;
        private double _lastLayoutWidth = -1;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register("ButtonWidth", typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        // Width of a compact button (icon + close, no title). Sized to fit those snugly: icon (16 + 3
        // margin) + close (13.5 + 3 margin) + the button's border/padding chrome (~8).
        public static DependencyProperty CompactButtonWidthProperty = DependencyProperty.Register(
            "CompactButtonWidth", typeof(double), typeof(TaskList), new PropertyMetadata(44.0));

        public double CompactButtonWidth
        {
            get { return (double)GetValue(CompactButtonWidthProperty); }
            set { SetValue(CompactButtonWidthProperty, value); }
        }

        public static DependencyProperty TasksProperty = DependencyProperty.Register("Tasks", typeof(Tasks), typeof(TaskList));

        public Tasks Tasks
        {
            get { return (Tasks)GetValue(TasksProperty); }
            set { SetValue(TasksProperty, value); }
        }

        public TaskList()
        {
            InitializeComponent();
        }

        private void SetStyles()
        {
            DefaultButtonWidth = Application.Current.FindResource("TaskButtonWidth") as double? ?? 0;
            MinButtonWidth = Application.Current.FindResource("TaskButtonMinWidth") as double? ?? 0;
            Thickness buttonMargin;

            if (Settings.Instance.Edge == (int)AppBarEdge.Left || Settings.Instance.Edge == (int)AppBarEdge.Right)
            {
                buttonMargin = Application.Current.FindResource("TaskButtonVerticalMargin") as Thickness? ?? new Thickness();
            }
            else
            {
                buttonMargin = Application.Current.FindResource("TaskButtonMargin") as Thickness? ?? new Thickness();
            }

            TaskButtonLeftMargin = buttonMargin.Left;
            TaskButtonRightMargin = buttonMargin.Right;
        }

        private void TaskList_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!isLoaded && Tasks != null)
            {
                if (Tasks.Windows != null)
                {
                    App app = Application.Current as App;
                    _virtualDesktopService = app?.VirtualDesktopService;

                    // Independent view (not the shared default view): show only taskbar windows that
                    // belong to the current virtual desktop, ordered by their order identifier, with
                    // live re-sort/re-filter on changes.
                    _windowsView = new ListCollectionView(Tasks.Windows)
                    {
                        Filter = w => w is ApplicationWindow window
                                      && IsTaskbarEligible(window)
                                      && (_currentDesktopWindows == null
                                          || _currentDesktopWindows.Contains(window.Handle)),
                        CustomSort = TasksOrderManager.Comparer,
                        IsLiveFiltering = true,
                        IsLiveSorting = true
                    };
                    _windowsView.LiveFilteringProperties.Add("ShowInTaskbar");
                    // Re-run the filter when a window's title changes: a real app that opens its main
                    // window with an empty title (filtered out by the empty-title guard in IsTaskbarEligible)
                    // must reappear the moment it sets a title, rather than staying hidden until the next
                    // unrelated refresh.
                    _windowsView.LiveFilteringProperties.Add("Title");
                    _windowsView.LiveSortingProperties.Add("Order");

                    TasksList.ItemsSource = _windowsView;
                    Tasks.Windows.CollectionChanged += GroupedWindows_CollectionChanged;

                    // Rebalance button width / centering when the *visible* set changes without the
                    // source collection changing — e.g. windows entering/leaving via the live filter on a
                    // virtual-desktop switch (which can arrive staggered as each window uncloaks).
                    // Subscribing to the ListCollectionView's CollectionChanged doesn't deliver those, so
                    // use LayoutUpdated (fires on every layout pass) guarded by a count/width change check.
                    LayoutUpdated += TaskList_OnLayoutUpdated;

                    // Re-filter when the user switches virtual desktops.
                    if (_virtualDesktopService != null)
                        _virtualDesktopService.DesktopChanged += VirtualDesktopService_DesktopChanged;

                    _desktopSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(400)
                    };
                    _desktopSettleTimer.Tick += DesktopSettleTimer_Tick;

                    TasksOrderManager orderManager = app?.TasksOrderManager;
                    if (orderManager != null && _dragHandler == null)
                    {
                        _dragHandler = new TabStripDragHandler(TasksList, _windowsView, orderManager);
                        _dragHandler.DragCompleted += DragHandler_DragCompleted;
                    }

                    // Re-align the task buttons live when the "center tasks" option is toggled.
                    Settings.Instance.PropertyChanged += Settings_PropertyChanged;

                    // Compute the initial current-desktop set.
                    ScheduleDesktopRefresh();
                }

                isLoaded = true;
            }

            SetStyles();
        }

        private void TaskList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (isLoaded && Tasks?.Windows != null)
            {
                Tasks.Windows.CollectionChanged -= GroupedWindows_CollectionChanged;
                Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            }

            LayoutUpdated -= TaskList_OnLayoutUpdated;

            _itemsPanel = null;

            if (_desktopSettleTimer != null)
            {
                _desktopSettleTimer.Stop();
                _desktopSettleTimer.Tick -= DesktopSettleTimer_Tick;
                _desktopSettleTimer = null;
            }

            if (_virtualDesktopService != null)
            {
                _virtualDesktopService.DesktopChanged -= VirtualDesktopService_DesktopChanged;
                _virtualDesktopService = null;
            }

            TasksList.ItemsSource = null;
            _windowsView = null;
            isLoaded = false;
        }

        // Whether a window should appear in the taskbar, independent of which virtual desktop it is on
        // (desktop membership is handled separately by the COM-resolved set). ManagedShell's ShowInTaskbar
        // is the authority, but it returns false for any DWM-cloaked window — which conflates two very
        // different cases: (a) a real app minimized on this desktop, whose cloak lingers after a desktop
        // switch until it is restored (a stale false we must override), and (b) a suspended/background UWP
        // window that should stay hidden. They are told apart by IsIconic: only a genuinely minimized
        // window is rescued, and only if it is a real taskbar window (CanAddToTaskbar) and not a UWP
        // "phantom" frame. The desktop-membership gate in the filter ensures this only ever applies to
        // windows already known to be on the current desktop.
        private static bool IsTaskbarEligible(ApplicationWindow window)
        {
            // A titleless, contentless window (e.g. the transient Explorer thumbnail/peek host that pops up
            // when hovering an Explorer button on the main taskbar — an explorer.exe window with the Explorer
            // icon and an empty title) would render as an empty-text button, so hide it. Real File Explorer
            // windows are exempt: they display the folder path even when their raw title is momentarily empty.
            if (string.IsNullOrWhiteSpace(window.Title)
                && !ExplorerPathHelper.IsFileExplorerWindow(window.Handle))
                return false;

            if (window.ShowInTaskbar)
                return true;

            return NativeMethods.IsIconic(window.Handle)
                   && window.CanAddToTaskbar
                   && !IsUwpPhantom(window);
        }

        // A cloaked/parked UWP shell frame (an ApplicationFrameWindow / CoreWindow without WS_EX_WINDOWEDGE)
        // that ManagedShell's getShowInTaskbar hides; mirror that check so the eligibility fallback above
        // doesn't resurrect these. Plain P/Invokes only (no COM), so safe to call from the filter.
        private static bool IsUwpPhantom(ApplicationWindow window)
        {
            try
            {
                var cn = new System.Text.StringBuilder(256);
                NativeMethods.GetClassName(window.Handle, cn, cn.Capacity);
                string className = cn.ToString();
                if (className == "ApplicationFrameWindow" || className == "Windows.UI.Core.CoreWindow")
                    return (window.ExtendedWindowStyles & (int)NativeMethods.ExtendedWindowStyles.WS_EX_WINDOWEDGE) == 0;
            }
            catch { }

            return false;
        }

        // Current virtual desktop changed: recompute which windows belong to it and re-filter.
        private void VirtualDesktopService_DesktopChanged(object sender, EventArgs e)
        {
            ScheduleDesktopRefresh();

            // The COM "is on current desktop" result can be momentarily unsettled right at the switch,
            // so recompute once more shortly after.
            _desktopSettleTimer?.Stop();
            _desktopSettleTimer?.Start();
        }

        private void DesktopSettleTimer_Tick(object sender, EventArgs e)
        {
            _desktopSettleTimer?.Stop();
            if (isLoaded)
                ScheduleDesktopRefresh();
        }

        // Determining desktop membership calls into COM, which pumps the message loop; doing that on the
        // UI thread mid-refresh re-enters ListCollectionView live shaping and crashes. So snapshot the
        // handles on the UI thread, query on a background thread, then apply the result and refresh
        // (coalesced, so a burst of window events collapses to one pass). The filter only reads the
        // resulting set, so Refresh() itself never pumps.
        private void ScheduleDesktopRefresh()
        {
            if (_virtualDesktopService == null)
                return;

            // A query is already in flight: remember that state changed so we re-run once it finishes.
            if (_desktopRefreshScheduled)
            {
                _desktopRefreshDirty = true;
                return;
            }

            _desktopRefreshScheduled = true;
            _desktopRefreshDirty = false;

            VirtualDesktopService service = _virtualDesktopService;
            List<IntPtr> handles = Tasks?.Windows?.OfType<ApplicationWindow>()
                                        .Select(w => w.Handle).ToList() ?? new List<IntPtr>();

            Task.Run(() =>
            {
                HashSet<IntPtr> onCurrent = service.GetWindowsOnCurrentDesktop(handles);

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _desktopRefreshScheduled = false;
                    if (!isLoaded)
                        return;

                    _currentDesktopWindows = onCurrent; // null => show all (fail open)

                    // Refreshing regenerates the item containers; doing that during a drag destroys the
                    // dragged button's mouse capture and aborts the reorder. Defer until the drag ends.
                    if (_dragHandler?.IsDragging == true)
                    {
                        _refreshPendingAfterDrag = true;
                    }
                    else
                    {
                        _windowsView?.Refresh();
                        SetTaskButtonWidth();
                    }

                    // State changed while the query was running: run once more to catch up.
                    if (_desktopRefreshDirty)
                        ScheduleDesktopRefresh();
                }));
            });
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // A window opened/closed while dragging: don't relayout/refresh now (it would abort the
            // drag by regenerating the containers); catch up once the drag completes.
            if (_dragHandler?.IsDragging == true)
            {
                _refreshPendingAfterDrag = true;
                return;
            }

            SetTaskButtonWidth();

            // A window opened/closed: refresh which of them are on the current desktop so a newly opened
            // window on this desktop shows up (scheduled off this handler to stay off the COM re-entrancy path).
            ScheduleDesktopRefresh();

            // A freshly opened window's title and virtual-desktop assignment settle a moment after it
            // appears (e.g. Notepad++ shows "new 1" before the file path, which flips its display type),
            // so recompute once more shortly after to pick up the settled state.
            _desktopSettleTimer?.Stop();
            _desktopSettleTimer?.Start();
        }

        // Flush any refresh/relayout that was deferred because a drag was in progress.
        private void DragHandler_DragCompleted()
        {
            if (!_refreshPendingAfterDrag)
                return;

            _refreshPendingAfterDrag = false;
            SetTaskButtonWidth();
            ScheduleDesktopRefresh();
        }

        private void TaskList_OnLayoutUpdated(object sender, EventArgs e)
        {
            // A window added/removed during a drag changes the item count; recomputing button widths
            // then would disrupt the in-flight gesture. Leave the layout as captured until it ends.
            if (_dragHandler?.IsDragging == true)
                return;

            int count = TasksList.Items.Count;
            double width = TasksList.ActualWidth;
            if (count == _lastLayoutCount && Math.Abs(width - _lastLayoutWidth) < 0.5)
                return;

            _lastLayoutCount = count;
            _lastLayoutWidth = width;
            SetTaskButtonWidth();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.CenterTasksInTaskbar) ||
                e.PropertyName == nameof(Settings.CompactSingleInstanceTasks))
                SetTaskButtonWidth();
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetTaskButtonWidth();
        }

        private void SetTaskButtonWidth()
        {
            // Compact single-instance windows to just their icon + close button. Their width comes from
            // CompactButtonWidth (via the item-template binding); the rest of the bar's width is split
            // between the remaining (normal) buttons.
            HashSet<IntPtr> compact = ComputeCompactWindows();
            ApplyCompactFlags(compact);

            if (Settings.Instance.Edge == (int)AppBarEdge.Left || Settings.Instance.Edge == (int)AppBarEdge.Right)
            {
                ButtonWidth = ActualWidth;
                AlignTasks(false, 0);
                return;
            }

            if (TasksList.Items.Count == 0)
            {
                return;
            }

            double margin = TaskButtonLeftMargin + TaskButtonRightMargin;
            int compactCount = compact.Count;
            int normalCount = TasksList.Items.Count - compactCount;
            double compactFootprint = CompactButtonWidth + margin;
            double availableForNormal = TasksList.ActualWidth - compactCount * compactFootprint;

            double defaultWidth = DefaultButtonWidth + margin;
            double minWidth = MinButtonWidth + margin;

            // "Not full" = the normal buttons stay at their default width with room to spare; when full
            // they shrink to fit (or overflow), so centering no longer applies.
            bool notFull;

            if (normalCount > 0)
            {
                double maxWidth = availableForNormal / normalCount;
                notFull = maxWidth > defaultWidth;

                if (maxWidth > defaultWidth)
                {
                    ButtonWidth = DefaultButtonWidth;
                }
                else if (maxWidth < minWidth)
                {
                    // More windows than fit even at the minimum width: keep buttons at the minimum so the
                    // maximum number stay in the single visible row; the genuine overflow wraps to a
                    // second row that the (scroll-disabled) ScrollViewer clips away.
                    ButtonWidth = MinButtonWidth;
                }
                else
                {
                    ButtonWidth = Math.Floor(maxWidth);
                }
            }
            else
            {
                // Every visible window is compact; there are no normal buttons to size.
                ButtonWidth = DefaultButtonWidth;
                notFull = true;
            }

            double contentWidth = normalCount * (ButtonWidth + margin) + compactCount * compactFootprint;
            AlignTasks(notFull && Settings.Instance.CenterTasksInTaskbar, contentWidth);
        }

        // The set of windows (by handle) that should be shown compactly: a window that is the only one of
        // its executable currently on the bar and isn't File Explorer. Compacting is applied when the
        // option is on, or — even with it off — when the bar is crowded (so many tasks that not all fit
        // at their default width), where collapsing single-instance windows reclaims space.
        private HashSet<IntPtr> ComputeCompactWindows()
        {
            var compact = new HashSet<IntPtr>();

            if (Settings.Instance.Edge == (int)AppBarEdge.Left ||
                Settings.Instance.Edge == (int)AppBarEdge.Right)
                return compact;

            List<ApplicationWindow> windows = TasksList.Items.OfType<ApplicationWindow>().ToList();
            if (windows.Count == 0)
                return compact;

            if (!Settings.Instance.CompactSingleInstanceTasks && !IsBarCrowded(windows.Count))
                return compact;

            var countByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ApplicationWindow w in windows)
            {
                string file = w.WinFileName ?? "";
                if (file.Length == 0)
                    continue;
                countByFile[file] = countByFile.TryGetValue(file, out int c) ? c + 1 : 1;
            }

            foreach (ApplicationWindow w in windows)
            {
                string file = w.WinFileName ?? "";
                if (file.Length == 0 || IsFileExplorer(file))
                    continue;
                // A window whose title is a file path (shown right-trimmed to keep the file name) carries
                // meaningful text, so it is never collapsed to just an icon.
                if (w.TaskBarDisplayType == TaskBarDisplayType.Right)
                    continue;
                if (countByFile.TryGetValue(file, out int c) && c == 1)
                    compact.Add(w.Handle);
            }

            return compact;
        }

        private static bool IsFileExplorer(string winFileName) =>
            winFileName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase);

        // The bar is crowded when its windows wouldn't all fit at the default button width.
        private bool IsBarCrowded(int totalCount)
        {
            double footprint = DefaultButtonWidth + TaskButtonLeftMargin + TaskButtonRightMargin;
            return TasksList.ActualWidth > 0 && footprint > 0 && totalCount * footprint > TasksList.ActualWidth;
        }

        // Push the compact flag onto each realized task button so its labels collapse and its width
        // switches to CompactButtonWidth (both driven off TaskButton.IsCompact).
        private void ApplyCompactFlags(HashSet<IntPtr> compact)
        {
            for (int i = 0; i < TasksList.Items.Count; i++)
            {
                if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
                    continue;

                TaskButton button = FindVisualChild<TaskButton>(container);
                if (button?.DataContext is ApplicationWindow window)
                    button.IsCompact = compact.Contains(window.Handle);
            }
        }

        // Center the task buttons (bar not full + option on) or left-align them (default). We keep the
        // WrapPanel stretched and instead inset it from the left by half the free space: setting the
        // panel's HorizontalAlignment=Center misbehaves inside this (horizontally sizing) ScrollViewer —
        // the panel arranges at a stale/narrow width and extra buttons overflow to its right. A computed
        // left margin is deterministic regardless of the ScrollViewer's measure behavior. TasksList
        // (the ItemsControl) stays stretched, so TasksList.ActualWidth remains the full viewport width
        // and the button-width calculation above has no layout feedback loop.
        private void AlignTasks(bool center, double contentWidth)
        {
            WrapPanel panel = GetItemsPanel();
            if (panel == null)
                return;

            double leftInset = 0;
            if (center && contentWidth > 0)
                leftInset = Math.Max(0, (TasksList.ActualWidth - contentWidth) / 2);

            if (panel.Margin.Left != leftInset)
                panel.Margin = new Thickness(leftInset, 0, 0, 0);
        }

        private WrapPanel GetItemsPanel()
        {
            if (_itemsPanel != null)
                return _itemsPanel;

            _itemsPanel = FindVisualChild<WrapPanel>(TasksList);
            return _itemsPanel;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;

                T descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }
    }
}
