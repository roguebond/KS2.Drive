﻿using NLog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public class CacheManager
    {
        public EventHandler CacheRefreshed;

        private static Logger logger = LogManager.GetCurrentClassLogger();

        private Int32 CacheDurationInSeconds = 5;
        private CacheMode _mode;
        private bool _PreLoadFoldersInCache;

        private object CurrentRefreshLock = new object();
        private List<String> CurrentRefresh = new List<string>();

        private void AddRefreshTask(FileNode CFN)
        {
            lock (CurrentRefreshLock)
            {
                if (CurrentRefresh.Contains(CFN.LocalPath)) return;
                CurrentRefresh.Add(CFN.LocalPath);
                new Thread(() => InternalRefreshFolderCacheContent(CFN)).Start();
            }
        }

        private void RemoveRefreshTask(FileNode CFN)
        {
            lock (CurrentRefreshLock)
            {
                CurrentRefresh.Remove(CFN.LocalPath);
            }
        }

        private static object CacheLock = new object();
        //TODO : Add periodic MissingFileCache cleanup
        private Dictionary<String, DateTime> MissingFileCache = new Dictionary<string, DateTime>(); //Store the files that we know for sure that they do not exist
        private Dictionary<String, FileNode> FileNodeCache = new Dictionary<string, FileNode>();
        public ReadOnlyDictionary<String, FileNode> CacheContent
        {
            get
            {
                return new ReadOnlyDictionary<String, FileNode>(FileNodeCache);
            }
        }

        public CacheManager(CacheMode mode, bool PreLoadFoldersInCache)
        {
            this._mode = mode;
            this._PreLoadFoldersInCache = PreLoadFoldersInCache;
        }

        /// <summary>
        /// Look in the cache for the FileNode matching the path
        /// Return the FileNode if found or an information notifying that the path is known to be non-existent
        /// </summary>
        public (FileNode node, bool IsNonExistent) GetFileNodeNoLock(String FileOrFolderLocalPath)
        {
            if (_mode == CacheMode.Disabled) return (null, false);

            if (!FileNodeCache.ContainsKey(FileOrFolderLocalPath))
            {
                //Is there a valid mising file entry for this file
                if (MissingFileCache.ContainsKey(FileOrFolderLocalPath) && (DateTime.Now - MissingFileCache[FileOrFolderLocalPath]).TotalSeconds <= CacheDurationInSeconds)
                {
                    //Less than (CacheDurationInSeconds) seconds ago, the file was not existing
                    //We send the same answer
                    return (null, true);
                }
                else
                {
                    return (null, false);
                }
            }
            else
            {
                return (FileNodeCache[FileOrFolderLocalPath], false);
            }
        }

        public void AddFileNodeNoLock(FileNode node)
        {
            if (_mode == CacheMode.Disabled) return;

            FileNodeCache.Add(node.LocalPath, node);
            MissingFileCache.Remove(node.LocalPath); //Remove the file from the known missing files list
            CacheRefreshed?.Invoke(this, null);
        }

        /// <summary>
        /// Renomme une clé du dictionnaire
        /// </summary>
        public void RenameFileNodeKey(String PreviousKey, String NewKey)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.RenameKey(PreviousKey, NewKey);
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void RenameFolderSubElements(String OldFolderName, String NewFolderName)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                foreach (var FolderSubElement in FileNodeCache.Where(x => x.Key.StartsWith(OldFolderName + "\\")).ToList())
                {
                    String OldKeyName = FolderSubElement.Key;
                    FolderSubElement.Value.LocalPath = NewFolderName + FolderSubElement.Value.LocalPath.Substring(OldFolderName.Length);
                    FolderSubElement.Value.RepositoryPath = FileNode.ConvertLocalPathToRepositoryPath(FolderSubElement.Value.LocalPath);
                    String NewKeyName = FolderSubElement.Value.LocalPath;

                    FileNodeCache.RenameKey(OldKeyName, NewKeyName);

                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void DeleteFileNode(FileNode node)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                if ((node.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0)
                {
                    foreach (var s in FileNodeCache.Where(x => x.Key.StartsWith(node.LocalPath + "\\")).ToList())
                    {
                        FileNodeCache.Remove(s.Key);
                    }
                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void InvalidateFileNode(FileNode node)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                String ParentFolderPath = node.LocalPath.Substring(0, node.LocalPath.LastIndexOf(@"\"));
                FileNodeCache[ParentFolderPath].IsParsed = false;
            }
        }

        /// <summary>
        /// Return folder content in the form of a list fo FileNodes
        /// If the folder as not already been parsed, we parse it from the server
        /// If the folder has been parsed, we serve content from the cache and update the cache from the server in a background task. So that we have a refreshed view for next call
        /// </summary>
        public (bool Success, List<Tuple<String, FileNode>> Content, String ErrorMessage) GetFolderContent(FileNode CurrentFolder, String Marker, LogWriter l)
        {
            List<FileNode> FileNodeToRefreshList = new List<FileNode>();
            List<Tuple<String, FileNode>> ReturnList = null;

            l.Append("Start GetFolderContent");

            if (_mode == CacheMode.Disabled)
            {
                var Result = InternalGetFolderContent(CurrentFolder, Marker);
                if (!Result.Success) return Result;
                else return (true, Result.Content, null);
            }

            lock (CacheLock)
            {
                if (!CurrentFolder.IsParsed)
                {
                    l.Append("Start Alf request");
                    var Result = InternalGetFolderContent(CurrentFolder, Marker);
                    l.Append("End Alf request");
                    if (!Result.Success) return Result;

                    CurrentFolder.IsParsed = true;
                    CurrentFolder.LastRefresh = DateTime.Now;

                    //Mise en cache du contenu du répertoire
                    foreach (var Node in Result.Content)
                    {
                        if (Node.Item1 == "." || Node.Item1 == "..") continue; //Bypass special folders
                        this.AddFileNodeNoLock(Node.Item2);
                    }

                    ReturnList = Result.Content;
                }
                else
                {
                    l.Append("Start cache read");
                    String FolderNameForSearch = CurrentFolder.LocalPath;
                    if (FolderNameForSearch != "\\") FolderNameForSearch += "\\";
                    ReturnList = new List<Tuple<String, FileNode>>();
                    ReturnList.Add(new Tuple<string, FileNode>(".", CurrentFolder));
                    //TODO : Add .. from cache
                    ReturnList.AddRange(FileNodeCache.Where(x => x.Key != CurrentFolder.LocalPath && x.Key.StartsWith($"{FolderNameForSearch}") && x.Key.LastIndexOf('\\').Equals(FolderNameForSearch.Length - 1)).Select(x => new Tuple<String, FileNode>(x.Value.Name, x.Value)));
                    if ((DateTime.Now - CurrentFolder.LastRefresh).TotalSeconds > CacheDurationInSeconds) FileNodeToRefreshList.Add(CurrentFolder); //Refresh current directory if the cache is too old
                    l.Append("Start end read");
                }

                //Sort list by path (mandatory if we want to handle a potential marker correctly)
                ReturnList = ReturnList.OrderBy(x => x.Item1).ToList();

                if (!String.IsNullOrEmpty(Marker)) //Dealing with potential marker
                {
                    l.Append("Start marker filter");
                    var WantedTuple = ReturnList.FirstOrDefault(x => x.Item1.Equals(Marker));
                    var WantedTupleIndex = ReturnList.IndexOf(WantedTuple);
                    if (WantedTupleIndex + 1 < ReturnList.Count) ReturnList = ReturnList.GetRange(WantedTupleIndex + 1, ReturnList.Count - 1 - WantedTupleIndex);
                    else ReturnList.Clear();
                    l.Append("End marker filter");
                }

                if (this._PreLoadFoldersInCache)
                {
                    foreach (var FolderNode in ReturnList.Where(x => (x.Item2.FileInfo.FileAttributes & (UInt32)System.IO.FileAttributes.Directory) != 0))
                    {
                        if (FolderNode.Item1 == "." || FolderNode.Item1 == "..") continue; //Bypass special folders
                                                                                           //Pre-loading of sub-folders of current folder
                        if (!FileNodeCache[FolderNode.Item2.LocalPath].IsParsed) FileNodeToRefreshList.Add(FolderNode.Item2);
                    }
                }
            }

            foreach (var FileNodeToRefresh in FileNodeToRefreshList)
            {
                AddRefreshTask(FileNodeToRefresh);
            }

            l.Append("End GetFolderContent");
            return (true, ReturnList, null);
        }

        public void Clear()
        {
            FileNodeCache.Clear();
            FileNodeCache = null;
        }

        public void Lock()
        {
            Monitor.Enter(CacheLock);
        }

        public void Unlock()
        {
            Monitor.Exit(CacheLock);
        }

        private void InternalRefreshFolderCacheContent(FileNode CFN)
        {
            var Result = InternalGetFolderContent(CFN, null);
            RemoveRefreshTask(CFN); //Server parsing opeartion has been made. The filenode statut is now parsed
            if (!Result.Success) return;

            lock (CacheLock)
            {
                CFN.IsParsed = true;
                CFN.LastRefresh = DateTime.Now;

                if (FileNodeCache == null) return; //Handle the case when the thread is still performing while the class has been unloaded

                String Filter1 = FileNode.IsRepositoryRootPath(CFN.RepositoryPath) ? "\\" : CFN.LocalPath + "\\";

                //Refresh from server result
                foreach (var Node in Result.Content)
                {
                    if (Node.Item1 == "." || Node.Item1 == "..") continue;
                    if (!FileNodeCache.ContainsKey(Node.Item2.LocalPath))
                    {
                        this.AddFileNodeNoLock(Node.Item2);
                    }
                    else
                    {
                        //TODO : Refresh node with updated properties
                    }
                }

                //Supprimer les entrées de FileNodeCache qui ne sont plus dans Result.Content
                foreach (var s in FileNodeCache.Where(x => x.Key != CFN.LocalPath && x.Key.StartsWith(Filter1) && x.Key.LastIndexOf('\\').Equals(Filter1.Length - 1)).ToList())
                {
                    if (Result.Content.FirstOrDefault(x => x.Item2.LocalPath.Equals(s.Value.LocalPath)) == null)
                    {
                        DeleteFileNode(s.Value);
                    }
                }

                CFN.LastRefresh = DateTime.Now;
            }
        }

        private (bool Success, List<Tuple<String, FileNode>> Content, String ErrorMessage) InternalGetFolderContent(FileNode CFN, String Marker)
        {
            var Proxy = new WebDavClient2();
            List<Tuple<String, FileNode>> ChildrenFileNames = new List<Tuple<String, FileNode>>();

            if (!FileNode.IsRepositoryRootPath(CFN.RepositoryPath))
            {
                //if this is not the root directory add the dot entries
                if (Marker == null) ChildrenFileNames.Add(new Tuple<String, FileNode>(".", CFN));

                if (null == Marker || "." == Marker)
                {
                    String ParentPath = FileNode.ConvertRepositoryPathToLocalPath(FileNode.GetRepositoryParentPath(CFN.RepositoryPath));
                    if (ParentPath != null)
                    {
                        //RepositoryElement ParentElement;
                        try
                        {
                            var ParentElement = Proxy.GetRepositoryElement(ParentPath);
                            if (ParentElement != null) ChildrenFileNames.Add(new Tuple<String, FileNode>("..", new FileNode(ParentElement)));
                        }
                        catch { }
                    }
                }
            }

            IEnumerable<WebDAVClient.Model.Item> ItemsInFolder;

            try
            {
                ItemsInFolder = Proxy.List(CFN.RepositoryPath).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }

            foreach (var Child in ItemsInFolder)
            {
                var Element = new FileNode(Child);
                if (Element.RepositoryPath.Equals(CFN.RepositoryPath)) continue; //Bypass l'entrée correspondant à l'élément appelant
                ChildrenFileNames.Add(new Tuple<string, FileNode>(Element.Name, Element));
            }

            return (true, ChildrenFileNames, null);
        }

        public void AddMissingFileNoLock(String FileOrFolderPath)
        {
            if (_mode == CacheMode.Disabled) return;
            MissingFileCache.Remove(FileOrFolderPath);
            MissingFileCache.Add(FileOrFolderPath, DateTime.Now);
        }
    }
}
