using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Conflict : ObservableObject
    {
        public string Marker
        {
            get => _change.ConflictMarker;
        }

        public string Description
        {
            get => _change.ConflictDesc;
        }

        public Models.ConflictFileState State
        {
            get => _state;
            private set => SetProperty(ref _state, value);
        }

        public object Theirs
        {
            get => _theirs;
            private set => SetProperty(ref _theirs, value);
        }

        public object Mine
        {
            get => _mine;
            private set => SetProperty(ref _mine, value);
        }

        /// <summary>
        ///     The embedded merge editor, or null when the card should be shown instead.
        /// </summary>
        public MergeConflictEditor Editor
        {
            get => _editor;
            private set
            {
                var old = _editor;
                if (SetProperty(ref _editor, value))
                {
                    if (old != null)
                    {
                        old.DiscardRequested -= OnEditorDiscardRequested;
                        old.PropertyChanged -= OnEditorPropertyChanged;
                    }

                    if (value != null)
                    {
                        value.DiscardRequested += OnEditorDiscardRequested;
                        value.PropertyChanged += OnEditorPropertyChanged;
                    }

                    OnPropertyChanged(nameof(EmbeddedEditor));
                    OnPropertyChanged(nameof(IsEditorPoppedOut));
                    OnPropertyChanged(nameof(IsCardVisible));
                    OnPropertyChanged(nameof(IsPendingEditor));
                    _wc.NotifyMergeEditorChanged();
                }
            }
        }

        /// <summary>
        ///     True while the conflict state is still being queried.
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(IsCardVisible));
                    OnPropertyChanged(nameof(IsPendingEditor));
                    _wc.NotifyMergeEditorChanged();
                }
            }
        }

        public bool IsCardVisible
        {
            get => _editor == null && !_isLoading;
        }

        /// <summary>
        ///     True while we don't know yet but expect to show the embedded editor, so the commit area stays hidden
        ///     instead of flashing in and out.
        /// </summary>
        public bool IsPendingEditor
        {
            get => _isLoading &&
                _editor == null &&
                _change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified &&
                !Preferences.Instance.OpenMergeEditorInSeparateWindow;
        }

        /// <summary>
        ///     The editor to show inline, or null while it is popped out into its own window.
        /// </summary>
        public MergeConflictEditor EmbeddedEditor
        {
            get => _editor is { IsPoppedOut: false } ? _editor : null;
        }

        public bool IsEditorPoppedOut
        {
            get => _editor is { IsPoppedOut: true };
        }

        public string FilePath
        {
            get => _change.Path;
        }

        public Conflict(Repository repo, WorkingCopy wc, Models.Change change, Conflict previous = null)
        {
            _repo = repo;
            _wc = wc;
            _change = change;

            // Keep showing what we had for the same file while the state is re-queried, to avoid flicker on refresh.
            if (previous != null && previous.FilePath == change.Path && !previous._isLoading)
            {
                _state = previous._state;
                _mine = previous._mine;
                _theirs = previous._theirs;
                _head = previous._head;
                _isLoading = false;
                Editor = previous._editor;
            }
            else if (!Preferences.Instance.OpenMergeEditorInSeparateWindow)
            {
                // Show an editor we already have for this file right away.
                Editor = wc.TryGetCachedMergeEditor(change.Path);
            }

            Task.Run(async () =>
            {
                _head = new Commands.QuerySingleCommit(repo.FullPath, "HEAD").GetResult();

                var (mine, theirs) = wc.InProgressContext switch
                {
                    CherryPickInProgress cherryPick => (_head, cherryPick.Head),
                    RebaseInProgress rebase => (rebase.Onto, rebase.StoppedAt),
                    AmInProgress applying => (_head, (object)"Patch"),
                    RevertInProgress revert => (_head, revert.Head),
                    MergeInProgress merge => (_head, merge.Source),
                    _ => (_head, (object)"Stash or Patch"),
                };

                var state = Models.ConflictFileState.Unknown;
                if ((_change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified) && !Directory.Exists(Path.Combine(_repo.FullPath, _change.Path)))
                    state = await new Commands.QueryConflictFileState(repo.FullPath, change)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (_detached)
                        return;

                    State = state;
                    Mine = mine;
                    Theirs = theirs;
                    Editor = state == Models.ConflictFileState.UnmergedText && !Preferences.Instance.OpenMergeEditorInSeparateWindow
                        ? _wc.GetOrCreateMergeEditor(_head, _change.Path)
                        : null;
                    IsLoading = false;
                });
            });
        }

        public async Task UseTheirsAsync()
        {
            await _wc.UseTheirsAsync([_change]);
        }

        public async Task UseMineAsync()
        {
            await _wc.UseMineAsync([_change]);
        }

        public MergeConflictEditor CreateOpenMergeEditorRequest()
        {
            return _state == Models.ConflictFileState.UnmergedText ? new MergeConflictEditor(_repo, _head, _change.Path) : null;
        }

        public void Detach()
        {
            _detached = true;

            if (_editor != null)
            {
                _editor.DiscardRequested -= OnEditorDiscardRequested;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }
        }

        public async Task MergeExternalAsync()
        {
            if (_state == Models.ConflictFileState.UnmergedText)
                await _wc.UseExternalMergeToolAsync(_change);
        }

        private void OnEditorPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MergeConflictEditor.IsPoppedOut))
            {
                OnPropertyChanged(nameof(EmbeddedEditor));
                OnPropertyChanged(nameof(IsEditorPoppedOut));
                _wc.NotifyMergeEditorChanged();
            }
        }

        private void OnEditorDiscardRequested()
        {
            Editor = _wc.RecreateMergeEditor(_head, _change.Path);
        }

        private Repository _repo = null;
        private WorkingCopy _wc = null;
        private Models.Change _change = null;
        private Models.Commit _head = null;
        private Models.ConflictFileState _state = Models.ConflictFileState.Unknown;
        private object _mine = null;
        private object _theirs = null;
        private MergeConflictEditor _editor = null;
        private bool _isLoading = true;
        private bool _detached = false;
    }
}
