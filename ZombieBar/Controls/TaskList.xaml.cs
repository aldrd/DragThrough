using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ZombieBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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
                    // Independent view (not the shared default view): show only taskbar windows,
                    // ordered by their order identifier, with live re-sort/re-filter on changes.
                    _windowsView = new ListCollectionView(Tasks.Windows)
                    {
                        Filter = w => w is ApplicationWindow window && window.ShowInTaskbar,
                        CustomSort = TasksOrderManager.Comparer,
                        IsLiveFiltering = true,
                        IsLiveSorting = true
                    };
                    _windowsView.LiveFilteringProperties.Add("ShowInTaskbar");
                    _windowsView.LiveSortingProperties.Add("Order");

                    TasksList.ItemsSource = _windowsView;
                    Tasks.Windows.CollectionChanged += GroupedWindows_CollectionChanged;

                    TasksOrderManager orderManager = (Application.Current as App)?.TasksOrderManager;
                    if (orderManager != null && _dragHandler == null)
                        _dragHandler = new TabStripDragHandler(TasksList, _windowsView, orderManager);
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

            TasksList.ItemsSource = null;
            _windowsView = null;
            isLoaded = false;
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();
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
