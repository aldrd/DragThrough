using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ZombieBar.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        // Handles of windows on the current virtual desktop. The filter only does a set lookup here;
        // the actual (COM) desktop query is done off the hot path in RecomputeDesktopMembership,
        // because COM STA calls pump messages and would re-enter ListCollectionView's live shaping.
        // Null = not yet computed / no desktop info -> show everything (fail open).
        private HashSet<IntPtr> _currentDesktopWindows;
        private bool _desktopRefreshScheduled;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register("ButtonWidth", typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
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
                                      && window.ShowInTaskbar
                                      && (_currentDesktopWindows == null
                                          || _currentDesktopWindows.Contains(window.Handle)),
                        CustomSort = TasksOrderManager.Comparer,
                        IsLiveFiltering = true,
                        IsLiveSorting = true
                    };
                    _windowsView.LiveFilteringProperties.Add("ShowInTaskbar");
                    _windowsView.LiveSortingProperties.Add("Order");

                    TasksList.ItemsSource = _windowsView;
                    Tasks.Windows.CollectionChanged += GroupedWindows_CollectionChanged;

                    // Re-filter when the user switches virtual desktops.
                    if (_virtualDesktopService != null)
                        _virtualDesktopService.DesktopChanged += VirtualDesktopService_DesktopChanged;

                    TasksOrderManager orderManager = app?.TasksOrderManager;
                    if (orderManager != null && _dragHandler == null)
                        _dragHandler = new TabStripDragHandler(TasksList, _windowsView, orderManager);

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

        // Current virtual desktop changed: recompute which windows belong to it and re-filter.
        private void VirtualDesktopService_DesktopChanged(object sender, EventArgs e)
        {
            ScheduleDesktopRefresh();
        }

        // Determining desktop membership calls into COM, which pumps the message loop; doing that on
        // the UI thread mid-refresh re-enters ListCollectionView live shaping and crashes. So snapshot
        // the handles on the UI thread, query on a background thread, then apply the result and refresh
        // (coalesced, so an uncloak storm collapses to one pass). The filter only reads the resulting
        // set, so Refresh() itself never pumps.
        private void ScheduleDesktopRefresh()
        {
            if (_desktopRefreshScheduled || _virtualDesktopService == null)
                return;

            _desktopRefreshScheduled = true;

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
                    _windowsView?.Refresh();
                }));
            });
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();

            // A window opened/closed: refresh which of them are on the current desktop so a newly
            // opened window on this desktop shows up (scheduled off this handler to stay off the
            // ListCollectionView/COM re-entrancy path).
            ScheduleDesktopRefresh();
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetTaskButtonWidth();
        }

        private void SetTaskButtonWidth()
        {
            if (Settings.Instance.Edge == (int)AppBarEdge.Left || Settings.Instance.Edge == (int)AppBarEdge.Right)
            {
                ButtonWidth = ActualWidth;
                return;
            }

            if (TasksList.Items.Count == 0)
            {
                return;
            }

            double margin = TaskButtonLeftMargin + TaskButtonRightMargin;
            double maxWidth = TasksList.ActualWidth / TasksList.Items.Count;
            double defaultWidth = DefaultButtonWidth + margin;
            double minWidth = MinButtonWidth + margin;

            if (maxWidth > defaultWidth)
            {
                ButtonWidth = DefaultButtonWidth;
            }
            else if (maxWidth < minWidth)
            {
                ButtonWidth = Math.Ceiling(DefaultButtonWidth / 2);
            }
            else
            {
                ButtonWidth = Math.Floor(maxWidth);
            }
        }
    }
}
