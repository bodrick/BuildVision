using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

using AlekseyNagovitsyn.BuildVision.Core.Common;
using AlekseyNagovitsyn.BuildVision.Tool.Building;
using AlekseyNagovitsyn.BuildVision.Core.Logging;
using AlekseyNagovitsyn.BuildVision.Helpers;
using AlekseyNagovitsyn.BuildVision.Tool.DataGrid;
using AlekseyNagovitsyn.BuildVision.Tool.Models;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Indicators.Core;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Settings;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Settings.Columns;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Settings.Sorting;
using AlekseyNagovitsyn.BuildVision.Tool.Views.Settings;

using Process = System.Diagnostics.Process;
using ProjectItem = AlekseyNagovitsyn.BuildVision.Tool.Models.ProjectItem;
using SortDescription = AlekseyNagovitsyn.BuildVision.Tool.Models.Settings.Sorting.SortDescription;
using Microsoft.VisualStudio;

namespace AlekseyNagovitsyn.BuildVision.Tool.ViewModels
{
    public class ControlViewModel : BindableBase
    {
        private BuildState _buildState;
        private IBuildInfo _buildInfo;
        private ObservableCollection<DataGridColumn> _gridColumnsRef;

        public ControlModel Model { get; }

        public BuildProgressViewModel BuildProgressViewModel { get; }

        public ControlSettings ControlSettings { get; }

        public ControlTemplate ImageCurrentState
        {
            get => Model.ImageCurrentState;
            set => SetProperty(() => Model.ImageCurrentState, val => Model.ImageCurrentState = val, value);
        }

        public ControlTemplate ImageCurrentStateResult
        {
            get => Model.ImageCurrentStateResult;
            set => SetProperty(() => Model.ImageCurrentStateResult, val => Model.ImageCurrentStateResult = val, value);
        }

        public string TextCurrentState
        {
            get => Model.TextCurrentState;
            set => SetProperty(() => Model.TextCurrentState, val => Model.TextCurrentState = val, value);
        }

        public ProjectItem CurrentProject
        {
            get => Model.CurrentProject;
            set => SetProperty(() => Model.CurrentProject, val => Model.CurrentProject = val, value);
        }

        public ObservableCollection<ValueIndicator> ValueIndicators => Model.ValueIndicators;

        public SolutionItem SolutionItem => Model.SolutionItem; 

        public ObservableCollection<ProjectItem> ProjectsList => Model.SolutionItem.Projects; 

        public string GridGroupPropertyName
        {
            get { return ControlSettings.GridSettings.GroupPropertyName; }
            set
            {
                if (ControlSettings.GridSettings.GroupPropertyName != value)
                {
                    ControlSettings.GridSettings.GroupPropertyName = value;
                    OnPropertyChanged(nameof(GridGroupPropertyName));
                    OnPropertyChanged(nameof(GroupedProjectsList));
                    OnPropertyChanged(nameof(GridColumnsGroupMenuItems));
                    OnPropertyChanged(nameof(GridGroupHeaderName));
                }
            }
        }

        public string GridGroupHeaderName
        {
            get
            {
                if (string.IsNullOrEmpty(GridGroupPropertyName))
                    return string.Empty;

                return ControlSettings.GridSettings.Columns[GridGroupPropertyName].Header;
            }
        }

        public CompositeCollection GridColumnsGroupMenuItems =>  CreateContextMenu();

        private CompositeCollection CreateContextMenu()
        {
            var collection = new CompositeCollection();
            collection.Add(new MenuItem
            {
                Header = Resources.NoneMenuItem,
                Tag = string.Empty
            });

            foreach (GridColumnSettings column in ControlSettings.GridSettings.Columns)
            {
                if (!ColumnsManager.ColumnIsGroupable(column))
                    continue;

                string header = column.Header;
                var menuItem = new MenuItem
                {
                    Header = !string.IsNullOrEmpty(header)
                                ? header
                                : ColumnsManager.GetInitialColumnHeader(column),
                    Tag = column.PropertyNameId
                };

                collection.Add(menuItem);
            }

            foreach (MenuItem menuItem in collection)
            {
                menuItem.IsCheckable = false;
                menuItem.StaysOpenOnClick = false;
                menuItem.IsChecked = (GridGroupPropertyName == (string)menuItem.Tag);
                menuItem.Command = GridGroupPropertyMenuItemClicked;
                menuItem.CommandParameter = menuItem.Tag;
            }

            return collection;
        }
 
        public SortDescription GridSortDescription
        {
            get { return ControlSettings.GridSettings.SortDescription; }
            set
            {
                if (ControlSettings.GridSettings.SortDescription != value)
                {
                    ControlSettings.GridSettings.SortDescription = value;
                    OnPropertyChanged(nameof(GridSortDescription));
                    OnPropertyChanged(nameof(GroupedProjectsList));
                }
            }
        }

        // Should be initialized by View.
        public ObservableCollection<DataGridColumn> GridColumnsRef
        {
            set
            {
                if (_gridColumnsRef != value)
                {
                    _gridColumnsRef = value;
                    GenerateColumns();
                }
            }
        }

        // TODO: Rewrite using CollectionViewSource? 
        // http://stackoverflow.com/questions/11505283/re-sort-wpf-datagrid-after-bounded-data-has-changed
        public ListCollectionView GroupedProjectsList
        {
            get
            {
                var groupedList = new ListCollectionView(ProjectsList);

                if (!string.IsNullOrWhiteSpace(GridGroupPropertyName))
                {
                    Debug.Assert(groupedList.GroupDescriptions != null);
                    groupedList.GroupDescriptions.Add(new PropertyGroupDescription(GridGroupPropertyName));
                }

                groupedList.CustomSort = GetProjectItemSorter(GridSortDescription);

                return groupedList;
            }
        }

        public DataGridHeadersVisibility GridHeadersVisibility
        {
            get
            {
                return ControlSettings.GridSettings.ShowColumnsHeader
                    ? DataGridHeadersVisibility.Column
                    : DataGridHeadersVisibility.None;
            }
            set
            {
                bool showColumnsHeader = (value != DataGridHeadersVisibility.None);
                if (ControlSettings.GridSettings.ShowColumnsHeader != showColumnsHeader)
                {
                    ControlSettings.GridSettings.ShowColumnsHeader = showColumnsHeader;
                    OnPropertyChanged(nameof(GridHeadersVisibility));
                }
            }
        }

        private ProjectItem _selectedProjectItem;
        public ProjectItem SelectedProjectItem
        {
            get => _selectedProjectItem; 
            set => SetProperty(ref _selectedProjectItem, value);
        }

        public ControlViewModel(ControlModel model, IPackageContext packageContext)
        {
            Model = model;
            ControlSettings = packageContext.ControlSettings;
            BuildProgressViewModel = new BuildProgressViewModel(ControlSettings);
            packageContext.ControlSettingsChanged += OnControlSettingsChanged;
        }

        /// <summary>
        /// Uses as design-time ViewModel. 
        /// </summary>
        internal ControlViewModel()
        {
            Model = new ControlModel();
            ControlSettings = new ControlSettings();
            BuildProgressViewModel = new BuildProgressViewModel(ControlSettings);
        }

        private void OpenContainingFolder()
        {
            try
            {
                string dir = Path.GetDirectoryName(SelectedProjectItem.FullName);
                Debug.Assert(dir != null);
                Process.Start(dir);
            }
            catch (Exception ex)
            {
                ex.Trace(string.Format(
                    "Unable to open folder '{0}' containing the project '{1}'.",
                    SelectedProjectItem.FullName,
                    SelectedProjectItem.UniqueName));

                MessageBox.Show(
                    ex.Message + "\n\nSee log for details.",
                    Resources.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ReorderGrid(object obj)
        {
            var e = (DataGridSortingEventArgs)obj;

            ListSortDirection? oldSortDirection = e.Column.SortDirection;
            ListSortDirection? newSortDirection;
            switch (oldSortDirection)
            {
                case null:
                    newSortDirection = ListSortDirection.Ascending;
                    break;
                case ListSortDirection.Ascending:
                    newSortDirection = ListSortDirection.Descending;
                    break;
                case ListSortDirection.Descending:
                    newSortDirection = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            e.Handled = true;
            e.Column.SortDirection = newSortDirection;

            GridSortDescription = new SortDescription(newSortDirection.ToMedia(), e.Column.GetBindedProperty());
        }

        private static ProjectItemColumnSorter GetProjectItemSorter(SortDescription sortDescription)
        {
            SortOrder sortOrder = sortDescription.SortOrder;
            string sortPropertyName = sortDescription.SortPropertyName;

            if (sortOrder != SortOrder.None && !string.IsNullOrEmpty(sortPropertyName))
            {
                ListSortDirection? sortDirection = sortOrder.ToSystem();
                Debug.Assert(sortDirection != null);

                try
                {
                    return new ProjectItemColumnSorter(sortDirection.Value, sortPropertyName);
                }
                catch (PropertyNotFoundException ex)
                {
                    ex.Trace("Trying to sort Project Items by nonexistent property.");
                    return null;
                }
            }

            return null;
        }

        public void ResetIndicators(ResetIndicatorMode resetMode)
        {
            foreach (ValueIndicator indicator in ValueIndicators)
                indicator.ResetValue(resetMode);

            OnPropertyChanged(nameof(ValueIndicators));
        }

        public void UpdateIndicators(IBuildInfo buildContext)
        {
            foreach (ValueIndicator indicator in ValueIndicators)
                indicator.UpdateValue(buildContext);

            OnPropertyChanged(nameof(ValueIndicators));
        }

        public void GenerateColumns()
        {
            Debug.Assert(_gridColumnsRef != null);
            ColumnsManager.GenerateColumns(_gridColumnsRef, ControlSettings.GridSettings);
        }

        public void SyncColumnSettings()
        {
            Debug.Assert(_gridColumnsRef != null);
            ColumnsManager.SyncColumnSettings(_gridColumnsRef, ControlSettings.GridSettings);
        }

        private void OnControlSettingsChanged(ControlSettings settings)
        {
            ControlSettings.InitFrom(settings);

            GenerateColumns();

            if (_buildState == BuildState.Done)
            {
                Model.TextCurrentState = BuildMessages.GetBuildDoneMessage(Model.SolutionItem, _buildInfo, ControlSettings.BuildMessagesSettings);
            }

            // Raise all properties have changed.
            OnPropertyChanged(null);

            BuildProgressViewModel.ResetTaskBarInfo(false);
        }

        public void OnBuildProjectBegin()
        {
            BuildProgressViewModel.OnBuildProjectBegin();
        }

        public void OnBuildProjectDone(BuildedProject buildedProjectInfo)
        {
            bool success = buildedProjectInfo.Success.GetValueOrDefault(true);
            BuildProgressViewModel.OnBuildProjectDone(success);
        }

        public void OnBuildBegin(int projectsCount, IBuildInfo buildContext)
        {
            _buildState = BuildState.InProgress;
            _buildInfo = buildContext;
            BuildProgressViewModel.OnBuildBegin(projectsCount);
        }

        public void OnBuildDone(IBuildInfo buildInfo)
        {
            _buildInfo = buildInfo;
            _buildState = BuildState.Done;
            BuildProgressViewModel.OnBuildDone();
        }

        public void OnBuildCancelled(IBuildInfo buildInfo)
        {
            _buildInfo = buildInfo;
            BuildProgressViewModel.OnBuildCancelled();
        }

        private bool IsProjectItemEnabledForActions()
        {
            return (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.UniqueName) && !SelectedProjectItem.IsBatchBuildProject);
        }

        #region Commands

        public ICommand GridSorting => new RelayCommand(obj => ReorderGrid(obj));

        public ICommand GridGroupPropertyMenuItemClicked => new RelayCommand(obj => GridGroupPropertyName = (obj != null) ? obj.ToString() : string.Empty);

        public ICommand SelectedProjectOpenContainingFolderAction => new RelayCommand(obj => OpenContainingFolder(),
                canExecute: obj => (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.FullName)));
    
        public ICommand SelectedProjectCopyBuildOutputFilesToClipboardAction => new RelayCommand(
            obj => ProjectCopyBuildOutputFilesToClipBoard(SelectedProjectItem),
            canExecute: obj => (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.UniqueName) && !ControlSettings.ProjectItemSettings.CopyBuildOutputFileTypesToClipboard.IsEmpty));

        public ICommand SelectedProjectBuildAction => new RelayCommand(
            obj => RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.BuildCtx),
            canExecute: obj => IsProjectItemEnabledForActions());


        public ICommand SelectedProjectRebuildAction => new RelayCommand(
            obj => RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.RebuildCtx),
            canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectCleanAction => new RelayCommand(
            obj => RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.CleanCtx),
            canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand BuildSolutionAction => new RelayCommand(obj => BuildSolution());

        public ICommand RebuildSolutionAction => new RelayCommand(obj => RebuildSolution());

        public ICommand CleanSolutionAction => new RelayCommand(obj => CleanSolution());

        public ICommand CancelBuildSolutionAction => new RelayCommand(obj => CancelBuildSolution());

        public ICommand OpenGridColumnsSettingsAction => new RelayCommand(obj => ShowOptionPage(typeof(GridSettingsDialogPage)));

        public ICommand OpenGeneralSettingsAction => new RelayCommand(obj => ShowOptionPage(typeof(GeneralSettingsDialogPage)));

        #endregion

        public event Action<Type> ShowOptionPage;
        public event Action BuildSolution;
        public event Action CleanSolution;
        public event Action RebuildSolution;
        public event Action CancelBuildSolution;
        public event Action<ProjectItem> ProjectCopyBuildOutputFilesToClipBoard;
        public event Action<ProjectItem, int> RaiseCommandForSelectedProject;
    }
}