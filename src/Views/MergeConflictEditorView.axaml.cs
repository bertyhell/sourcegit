using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class MergeConflictEditorView : UserControl
    {
        public static readonly StyledProperty<bool> IsEmbeddedProperty =
            AvaloniaProperty.Register<MergeConflictEditorView, bool>(nameof(IsEmbedded));

        public bool IsEmbedded
        {
            get => GetValue(IsEmbeddedProperty);
            set => SetValue(IsEmbeddedProperty, value);
        }

        public static readonly DirectProperty<MergeConflictEditorView, Models.ConflictSelectedChunk> SelectedChunkProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictEditorView, Models.ConflictSelectedChunk>(
                nameof(SelectedChunk),
                static o => o.SelectedChunk,
                static (o, v) => o.SelectedChunk = v);

        public Models.ConflictSelectedChunk SelectedChunk
        {
            get => _selectedChunk;
            set => SetAndRaise(SelectedChunkProperty, ref _selectedChunk, value);
        }

        /// <summary>
        ///     Raised after the result has been written and staged.
        /// </summary>
        public event Action Saved;

        /// <summary>
        ///     Raised when the user presses Cancel while the editor is hosted in its own window.
        /// </summary>
        public event Action CancelRequested;

        public MergeConflictEditorView()
        {
            InitializeComponent();
            Bind(SelectedChunkProperty, new Binding("SelectedChunk", BindingMode.OneWay));
        }

        public bool IsScrollDriver(MergeConflictTextPresenter presenter)
        {
            if (presenter.IsPointerOver)
                return true;

            // Keyboard navigation drives scrolling only when the mouse is not over another panel.
            return presenter.IsKeyboardFocusWithin &&
                !OursPresenter.IsPointerOver &&
                !TheirsPresenter.IsPointerOver &&
                !ResultPresenter.IsPointerOver;
        }

        public void SyncScrollFrom(MergeConflictTextPresenter source)
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            if (!source.TryGetTopLine(out var line, out var fraction))
                return;

            var x = source.TextArea.TextView.HorizontalOffset;
            foreach (var presenter in new[] { OursPresenter, TheirsPresenter, ResultPresenter })
            {
                if (presenter == source)
                    continue;

                var target = vm.MapLine(source.PanelType, presenter.PanelType, line);
                presenter.ScrollToLineTop(target, fraction, x);
            }
        }

        public void ScrollAllTo(Models.ConflictPanelType panel, int line)
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            foreach (var presenter in new[] { OursPresenter, TheirsPresenter, ResultPresenter })
                presenter.ScrollToLineNearTop(vm.MapLine(panel, presenter.PanelType, line));
        }

        /// <summary>
        ///     Handles the editor's keyboard shortcuts. Returns true when the key was consumed.
        ///     Hosting windows forward their key events here so the shortcuts work without focus.
        /// </summary>
        public bool HandleShortcut(KeyEventArgs e)
        {
            var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

            if (e.Key == Key.Escape && DataContext is ViewModels.MergeConflictEditor { Error.Length: > 0 } withError)
            {
                withError.ClearErrorMessage();
                return true;
            }

            if (e.Key == Key.F7 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                GotoConflict(e.KeyModifiers == KeyModifiers.None);
                return true;
            }

            if (e.KeyModifiers == cmd && e.Key is Key.Up or Key.Down)
            {
                GotoConflict(e.Key == Key.Down);
                return true;
            }

            if (e.KeyModifiers == cmd && e.Key == Key.S)
            {
                if (DataContext is ViewModels.MergeConflictEditor { UnsolvedCount: 0 })
                    SaveAndStage();
                return true;
            }

            return false;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            if (_vm != null)
                _vm.RegionsChanged -= OnRegionsChanged;

            base.OnDataContextChanged(e);

            _vm = DataContext as ViewModels.MergeConflictEditor;
            if (_vm != null)
                _vm.RegionsChanged += OnRegionsChanged;

            if (CollapseUnchangedToggle.IsChecked == true)
                CollapseUnchangedToggle.IsChecked = false;
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            if (_vm != null)
            {
                _vm.RegionsChanged -= OnRegionsChanged;
                _vm = null;
            }

            base.OnUnloaded(e);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (_vm == null && DataContext is ViewModels.MergeConflictEditor vm)
            {
                _vm = vm;
                _vm.RegionsChanged += OnRegionsChanged;
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SelectedChunkProperty)
                UpdatePopupVisibility();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (HandleShortcut(e))
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void OnRegionsChanged()
        {
            OursPresenter.InvalidateAll();
            TheirsPresenter.InvalidateAll();
            ResultPresenter.InvalidateAll();
            Minimap.InvalidateVisual();
        }

        private void OnGotoPrevConflict(object sender, RoutedEventArgs e)
        {
            GotoConflict(false);
            e.Handled = true;
        }

        private void OnGotoNextConflict(object sender, RoutedEventArgs e)
        {
            GotoConflict(true);
            e.Handled = true;
        }

        private void GotoConflict(bool next)
        {
            if (!IsLoaded || DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            var idx = vm.GotoConflict(next);
            if (idx < 0)
                return;

            var (first, _) = vm.GetResultRange(idx);
            var doc = ResultPresenter.Document;
            if (first < doc.LineCount)
                ResultPresenter.TextArea.Caret.Offset = doc.GetLineByNumber(first + 1).Offset;

            ScrollAllTo(Models.ConflictPanelType.Result, first);
        }

        private void OnCollapseUnchangedChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            var rowRanges = new List<(int, int)>();
            var theirsRanges = new List<(int, int)>();
            var resultRanges = new List<(int, int)>();

            if (CollapseUnchangedToggle.IsChecked == true && vm.ConflictRegions.Count > 0)
            {
                const int context = 3;
                var lineCount = vm.OursLines.Count;
                var gapStart = 0;
                for (var i = 0; i <= vm.ConflictRegions.Count; i++)
                {
                    var isEnd = i == vm.ConflictRegions.Count;
                    var region = isEnd ? null : vm.ConflictRegions[i];
                    var gapEnd = isEnd ? lineCount - 1 : region.OursFirst - 1;
                    var s = gapStart == 0 ? 0 : gapStart + context;
                    var t = isEnd ? gapEnd : gapEnd - context;

                    // Don't bother folding just a couple of lines.
                    if (t - s >= 2)
                    {
                        rowRanges.Add((s, t));
                        theirsRanges.Add((vm.MapLine(Models.ConflictPanelType.Ours, Models.ConflictPanelType.Theirs, s), vm.MapLine(Models.ConflictPanelType.Ours, Models.ConflictPanelType.Theirs, t)));
                        resultRanges.Add((vm.MapLine(Models.ConflictPanelType.Ours, Models.ConflictPanelType.Result, s), vm.MapLine(Models.ConflictPanelType.Ours, Models.ConflictPanelType.Result, t)));
                    }

                    if (!isEnd)
                        gapStart = region.OursFirst + region.OursRowCount;
                }
            }

            OursPresenter.SetFoldings(rowRanges);
            TheirsPresenter.SetFoldings(theirsRanges);
            ResultPresenter.SetFoldings(resultRanges);
            SyncScrollFrom(OursPresenter);
        }

        private void OnBranchBadgePressed(object sender, PointerPressedEventArgs e)
        {
            if (DataContext is ViewModels.MergeConflictEditor vm && sender is Control { Tag: Models.Commit commit })
                vm.NavigateToCommit(commit);

            e.Handled = true;
        }

        private async void OnOpenExternalMergeTool(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (DataContext is ViewModels.MergeConflictEditor vm)
                await vm.OpenExternalMergeToolAsync();
        }

        private void OnPopOut(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (DataContext is not ViewModels.MergeConflictEditor vm || vm.IsPoppedOut)
                return;

            // Show the window first: marking it popped out removes this view from the main window.
            this.ShowWindow(new MergeConflictEditor() { DataContext = vm });
            vm.IsPoppedOut = true;
        }

        private void OnSaveAndStage(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            SaveAndStage();
        }

        private async void SaveAndStage()
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            var success = await vm.SaveAndStageAsync();
            if (success)
                Saved?.Invoke();
        }

        private async void OnSecondaryAction(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (!IsEmbedded)
            {
                CancelRequested?.Invoke();
                return;
            }

            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            if (vm.IsDirty && TopLevel.GetTopLevel(this) is Window owner)
            {
                var confirm = new Confirm();
                confirm.SetData(App.Text("MergeConflictEditor.UnsavedChanges"), Models.ConfirmButtonType.YesNo);
                if (!await confirm.ShowDialog<bool>(owner))
                    return;
            }

            vm.RequestDiscard();
        }

        private void UpdatePopupVisibility()
        {
            MinePopup.IsVisible = false;
            TheirsPopup.IsVisible = false;
            ResultPopup.IsVisible = false;
            ResultUndoPopup.IsVisible = false;

            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            var chunk = vm.SelectedChunk;
            if (chunk == null)
                return;

            var presenter = chunk.Panel switch
            {
                Models.ConflictPanelType.Ours => OursPresenter,
                Models.ConflictPanelType.Theirs => TheirsPresenter,
                Models.ConflictPanelType.Result => ResultPresenter,
                _ => null
            };

            var popup = chunk.Panel switch
            {
                Models.ConflictPanelType.Ours => MinePopup,
                Models.ConflictPanelType.Theirs => TheirsPopup,
                Models.ConflictPanelType.Result => chunk.IsResolved ? ResultUndoPopup : ResultPopup,
                _ => null
            };

            if (popup != null && presenter != null)
            {
                // Position popup - clamp to visible area
                var top = chunk.Y + (chunk.Height >= 36 ? 8 : 2);
                var popupHeight = popup.Bounds.Height > 0 ? popup.Bounds.Height : 32;
                var presenterHeight = presenter.Bounds.Height;
                top = Math.Max(4, Math.Min(top, presenterHeight - popupHeight - 4));

                popup.Margin = new Thickness(0, top, 24, 0);
                popup.IsVisible = true;
            }
        }

        private Models.ConflictSelectedChunk _selectedChunk = null;
        private ViewModels.MergeConflictEditor _vm = null;
    }
}
