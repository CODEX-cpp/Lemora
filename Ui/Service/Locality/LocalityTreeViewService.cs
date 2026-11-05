using Lemora.Utils.Tracing;
using Newtonsoft.Json;
using Shawn.Utils;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ServerTreeViewModel = Lemora.View.ServerView.Tree.ServerTreeViewModel;

namespace Lemora.Service.Locality
{
    public class LocalityTreeViewSettings
    {
        public EnumServerOrderBy ServerOrderBy = EnumServerOrderBy.IdAsc;
        public Dictionary<string, int> CustomNodeOrder = new Dictionary<string, int>();
        /// <summary>
        /// Dictionary to store tree node expansion states
        /// Key: full path string of the tree folder node (e.g., "LocalDataSource->Folder1->SubFolder")
        /// Value: whether the node is expanded
        /// </summary>
        public Dictionary<string, bool> TreeNodeExpansionStates = new Dictionary<string, bool>();
    }

    public static class LocalityTreeViewService
    {
        public static string JsonPath => Path.Combine(AppPathHelper.Instance.LocalityDirPath, ".tree_view.json");

        private static LocalityTreeViewSettings? _settings;
        public static LocalityTreeViewSettings Settings
        {
            get
            {
                if (_settings == null || !_dbLoaded)
                {
                    Load();
                }
                return _settings!;
            }
            private set => _settings = value;
        }


        private const string DbKey = "tree_view";
        private static bool _dbLoaded = false;

        public static void Load()
        {
            var json = AppDbStore.ReadOrMigrate(DbKey, JsonPath, out var ready);
            _dbLoaded = ready;   // se il database non e' pronto si lavora in memoria e si riprova alla prossima lettura
            try
            {
                var tmp = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<LocalityTreeViewSettings>(json);
                tmp ??= new LocalityTreeViewSettings();
                _settings = tmp;
            }
            catch
            {
                _settings = new LocalityTreeViewSettings();
            }
        }

        public static void Save()
        {
            if (!_dbLoaded) return;   // se il database non e' stato letto non lo si sovrascrive
            RetryHelper.Try(() => { AppDbStore.Set(DbKey, JsonConvert.SerializeObject(Settings)); }, actionOnError: exception => UnifyTracing.Error(exception));
        }

        public static void ServerOrderBySet(EnumServerOrderBy value)
        {
            if (Settings.ServerOrderBy == value) return;
            Settings.ServerOrderBy = value;
            Save();
        }

        public static void SaveExpansionStates(ObservableCollection<ServerTreeViewModel.TreeNode> roots)
        {
            var currentValidStates = new Dictionary<string, bool>();
            // Recursively apply cached states and collect current valid folder paths
            {
                var stack = new Stack<ServerTreeViewModel.TreeNode>();
                foreach (var node in roots)
                {
                    stack.Push(node);
                }
                while (stack.Count > 0)
                {
                    var currentNode = stack.Pop();
                    if (currentNode.IsFolder && !string.IsNullOrEmpty(currentNode.FullPath))
                    {
                        currentValidStates[currentNode.FullPath] = currentNode.IsExpanded;
                    }
                    // Push all child nodes onto the stack (in reverse order to maintain original traversal order)
                    if (currentNode.Children.Count > 0)
                    {
                        for (int i = currentNode.Children.Count - 1; i >= 0; i--)
                        {
                            stack.Push(currentNode.Children[i]);
                        }
                    }
                }
            }
            // Rebuild the cache
            Settings.TreeNodeExpansionStates = currentValidStates;
            Save();
        }


        /// <summary>
        /// Dimentica la cartella (sotto qualunque radice): l'albero ricrea le cartelle vuote da questo elenco,
        /// quindi una cartella Teams svuotata dal server restava per sempre, anche dopo il riavvio.
        /// </summary>
        public static void ForgetFolder(IReadOnlyList<string> nodes)
        {
            var keys = new List<string>();
            foreach (var key in Settings.TreeNodeExpansionStates.Keys)
            {
                var parts = key.Split(ServerTreeViewModel.FullPathSeparator, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length - 1 != nodes.Count) continue;
                var same = true;
                for (var i = 0; i < nodes.Count && same; i++) same = parts[i + 1] == nodes[i];
                if (same) keys.Add(key);
            }
            if (keys.Count == 0) return;
            foreach (var k in keys) Settings.TreeNodeExpansionStates.Remove(k);
            Save();
        }

        public static void LoadExpansionStates(ObservableCollection<ServerTreeViewModel.TreeNode> roots)
        {
            var cachedStates = Settings.TreeNodeExpansionStates;
            var currentValidStates = new Dictionary<string, bool>();
            // Recursively apply cached states and collect current valid folder paths
            var stack = new Stack<ServerTreeViewModel.TreeNode>();
            foreach (var node in roots)
            {
                stack.Push(node);
            }
            while (stack.Count > 0)
            {
                var currentNode = stack.Pop();
                if (currentNode.IsFolder && !string.IsNullOrEmpty(currentNode.FullPath))
                {
                    // Get cached expansion state or default to true
                    var savedExpansionState = cachedStates.GetValueOrDefault(currentNode.FullPath, true);
                    // Apply the state without triggering save
                    currentNode._isExpanded = savedExpansionState;
                    currentNode.RaisePropertyChanged(nameof(ServerTreeViewModel.TreeNode.IsExpanded));
                    currentValidStates[currentNode.FullPath] = currentNode.IsExpanded;
                }

                // Push all child nodes onto the stack (in reverse order to maintain original traversal order)
                if (currentNode.Children.Count > 0)
                {
                    for (int i = currentNode.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(currentNode.Children[i]);
                    }
                }
            }
            // Rebuild the cache
            Settings.TreeNodeExpansionStates = currentValidStates;
            Save();
        }
    }
}
