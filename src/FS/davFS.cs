﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;
using System.Net;
using System.Linq;
using NLog;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using WebDAVClient.Helpers;
using System.Net.Http;

namespace KS2Drive.FS
{
    public class DavFS : FileSystemBase
    {
        public ConcurrentQueue<DebugMessage> DebugMessageQueue = new ConcurrentQueue<DebugMessage>();
        public EventHandler DebugMessagePosted;
        public EventHandler CacheRefreshed;
        private static Logger logger = LogManager.GetCurrentClassLogger();

        private UInt32 MaxFileNodes;
        private UInt32 MaxFileSize;
        private String VolumeLabel;
        private String DAVServer;

        public string DAVServeurAuthority { get; }

        private FlushMode FlushMode;
        private KernelCacheMode kernelCacheMode;
        private String DAVLogin;
        private String DAVPassword;
        private WebDAVMode WebDAVMode;
        private String DocumentLibraryPath;

        public const UInt16 MEMFS_SECTOR_SIZE = 4096;
        public const UInt16 MEMFS_SECTORS_PER_ALLOCATION_UNIT = 1;

        #region Internal Cache management

        private object CacheLock = new object();
        public List<FileNode> FileNodeCache = new List<FileNode>();

        private List<FileNode> GetFolderContentFromCache(String FolderName)
        {
            lock (CacheLock)
            {
                var KnownFolder = FileNodeCache.FirstOrDefault(x => x.LocalPath.Equals(FolderName));
                if (KnownFolder == null || !KnownFolder.IsParsed) return null;

                if (FolderName != "\\") FolderName += "\\";

                List<FileNode> ReturnList = new List<FileNode>();
                ReturnList.AddRange(FileNodeCache.Where(x => x.LocalPath != FolderName && x.LocalPath.StartsWith($"{FolderName}") && x.LocalPath.LastIndexOf('\\') == FolderName.Length - 1));
                return ReturnList;
            }
        }

        private void UpdateReservedFileNodeInCache(FileNode node)
        {
            lock (CacheLock)
            {
                FileNodeCache.RemoveAll(x => x.LocalPath.Equals(node.LocalPath));
                FileNodeCache.Add(node);
            }
        }

        private void RemoveReservedFileNodeFromCache(String FileOrFolderLocalPath)
        {
            lock (CacheLock)
            {
                FileNodeCache.RemoveAll(x => x.LocalPath.Equals(FileOrFolderLocalPath));
            }
        }

        private FileNode GetFileNodeFromCache(String FileOrFolderLocalPath, bool ReserveIfNull = false)
        {
            lock (CacheLock)
            {
                var KnownNode = FileNodeCache.FirstOrDefault(x => x.LocalPath.Equals(FileOrFolderLocalPath));
                if (!ReserveIfNull) return KnownNode;

                if (KnownNode == null)
                {
                    FileNodeCache.Add(new FileNode() { LocalPath = FileOrFolderLocalPath });
                    return null;
                }
                else
                {
                    return KnownNode;
                }
            }
        }

        private void AddFileNodeToCache(FileNode node)
        {
            lock (CacheLock)
            {
                FileNodeCache.Add(node);
                CacheRefreshed?.Invoke(this, null);
            }
        }

        private void RenameFolderSubElementsInCache(String OldFolderName, String NewFolderName)
        {
            lock (CacheLock)
            {
                foreach (var FolderSubElement in FileNodeCache.Where(x => x.LocalPath.StartsWith(OldFolderName + "\\")))
                {
                    FolderSubElement.LocalPath = NewFolderName + FolderSubElement.LocalPath.Substring(OldFolderName.Length);
                    FolderSubElement.RepositoryPath = ConvertLocalPathToRepositoryPath(FolderSubElement.LocalPath);
                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        private void DeleteFileNodeFromCache(FileNode node)
        {
            lock (CacheLock)
            {
                FileNodeCache.Remove(node);
                if ((node.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0)
                {
                    FileNodeCache.RemoveAll(x => x.LocalPath.StartsWith(node.LocalPath + "\\"));
                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        private void InvalidateFileNodeInCache(FileNode node)
        {
            lock(CacheLock)
            {
                FileNodeCache.Remove(node);
                String ParentFolderPath = node.LocalPath.Substring(0, node.LocalPath.LastIndexOf(@"\"));
                var ParentFromCache = FileNodeCache.FirstOrDefault(x => x.LocalPath.Equals(ParentFolderPath));
                if (ParentFromCache != null) ParentFromCache.IsParsed = false;
            }
        }

        #endregion

        public DavFS(WebDAVMode webDAVMode, String DavServerURL, FlushMode flushMode, KernelCacheMode kernelCacheMode, String DAVLogin, String DAVPassword, bool UseProxy = false, String ProxyURL = "", bool UseProxyAuthentication = false, String ProxyLogin = "", String ProxyPassword = "")
        {
            if (UseProxy)
            {
                if (UseProxyAuthentication) WebRequest.DefaultWebProxy = new WebProxy(ProxyURL, false, null, new NetworkCredential(ProxyLogin, ProxyPassword));
                else WebRequest.DefaultWebProxy = new WebProxy(ProxyURL, false);
            }

            //TEMP
            //WebRequest.DefaultWebProxy = new WebProxy("http://10.10.100.102:8888", false);
            //TEMP

            this.MaxFileNodes = 500000;
            this.MaxFileSize = UInt32.MaxValue;
            this.FlushMode = flushMode;
            this.WebDAVMode = webDAVMode;
            this.kernelCacheMode = kernelCacheMode;

            var DavServerURI = new Uri(DavServerURL);
            this.DAVServer = DavServerURI.GetLeftPart(UriPartial.Authority);
            this.DAVServeurAuthority = DavServerURI.DnsSafeHost;
            this.DocumentLibraryPath = DavServerURI.PathAndQuery;

            FileNode.DocumentLibraryPath = this.DocumentLibraryPath;

            this.DAVLogin = DAVLogin;
            this.DAVPassword = DAVPassword;

            //Test Connection
            var Proxy = GenerateProxy();
            try
            {
                var LisTest = Proxy.List("/").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new Exception($"Impossible de se connecter au serveur : {ex.Message}");
            }
        }

        /// <summary>
        /// Defini les paramétres généraux du systéme de fichiers
        /// </summary>
        public override Int32 Init(Object Host0)
        {
            FileSystemHost Host = (FileSystemHost)Host0;

            Host.FileInfoTimeout = unchecked((UInt32)(Int32)(this.kernelCacheMode));
            Host.FileSystemName = "davFS";
            Host.Prefix = $@"\{this.DAVServeurAuthority}\dav"; //mount as network drive
            Host.SectorSize = DavFS.MEMFS_SECTOR_SIZE;
            Host.SectorsPerAllocationUnit = DavFS.MEMFS_SECTORS_PER_ALLOCATION_UNIT;
            Host.VolumeCreationTime = (UInt64)DateTime.Now.ToFileTimeUtc();
            Host.VolumeSerialNumber = (UInt32)(Host.VolumeCreationTime / (10000 * 1000));
            Host.CaseSensitiveSearch = false;
            Host.CasePreservedNames = true;
            Host.UnicodeOnDisk = true;
            Host.PersistentAcls = false;
            Host.ReparsePoints = false;
            Host.ReparsePointsAccessCheck = false;
            Host.NamedStreams = false;
            Host.PostCleanupWhenModifiedOnly = true; //Decide wheither to fire a cleanup message a every Create / open or only if the file was modified

            return STATUS_SUCCESS;
        }

        /// <summary>
        /// Informations sur le volume (tailles, nom)
        /// </summary>
        public override Int32 GetVolumeInfo(
            out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default(VolumeInfo);
            VolumeInfo.TotalSize = MaxFileNodes * (UInt64)MaxFileSize;
            VolumeInfo.FreeSize = MaxFileNodes * (UInt64)MaxFileSize;
            VolumeInfo.SetVolumeLabel(VolumeLabel);
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// Affecte un nom au volume et retourne un volume info
        /// </summary>
        /// <param name="VolumeLabel"></param>
        /// <param name="VolumeInfo"></param>
        /// <returns></returns>
        public override Int32 SetVolumeLabel(
            String VolumeLabel,
            out VolumeInfo VolumeInfo)
        {
            this.VolumeLabel = VolumeLabel;
            return GetVolumeInfo(out VolumeInfo);
        }

        /// <summary>
        /// Retrieve a file or folder from the remote repo
        /// Return either a RepositoryElement or a FileSystem Error Message
        /// </summary>
        private WebDAVClient.Model.Item GetRepositoryElement(String LocalFileName)
        {
            String RepositoryDocumentName = ConvertLocalPathToRepositoryPath(LocalFileName);
            WebDAVClient.Model.Item RepositoryElement = null;

            var Proxy = GenerateProxy();

            if (RepositoryDocumentName.Contains("."))
            {
                //We assume the FileName refers to a file
                try
                {
                    RepositoryElement = Proxy.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
                    return RepositoryElement;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                {
                    return null;
                }
                catch (WebDAVException ex)
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            else
            {
                //We assume it's a folder
                try
                {
                    RepositoryElement = Proxy.GetFolder(RepositoryDocumentName).GetAwaiter().GetResult();
                    if (IsRepositoryRootPath(RepositoryDocumentName)) RepositoryElement.DisplayName = "";
                    return RepositoryElement;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                {
                    //Try as a file
                    try
                    {
                        RepositoryElement = Proxy.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
                        return RepositoryElement;
                    }
                    catch (WebDAVException ex1) when (ex1.GetHttpCode() == 404)
                    {
                        return null;
                    }
                    catch (WebDAVException ex1)
                    {
                        throw ex1;
                    }
                    catch (Exception ex1)
                    {
                        throw ex1;
                    }
                }
                catch (WebDAVException ex)
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

        /// <summary>
        /// GetSecurityByName is used by WinFsp to retrieve essential metadata about a file to be opened, such as its attributes and security descriptor.
        /// </summary>
        public override Int32 GetSecurityByName(
            String FileName,
            out UInt32 FileAttributes/* or ReparsePointIndex */,
            ref Byte[] SecurityDescriptor)
        {
            //if (FileName.ToLower().Contains("desktop.ini") || (FileName.ToLower().Contains("autorun.inf")))
            //{
            //    FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
            //    return STATUS_OBJECT_NAME_NOT_FOUND;
            //}

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, "", FileName);

            var KnownNode = GetFileNodeFromCache(FileName, true); //If no object in cache, reserve it (to avoid concurrency issues)
            if (KnownNode == null)
            {
                WebDAVClient.Model.Item FoundElement;

                try
                {
                    FoundElement = GetRepositoryElement(FileName);
                }
                catch (WebDAVException ex)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                    DebugEnd(OperationId, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                    return STATUS_OBJECT_NAME_NOT_FOUND;
                }
                catch (HttpRequestException ex)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                    DebugEnd(OperationId, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                    return FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND;
                }

                if (FoundElement == null)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                    DebugEnd(OperationId, "STATUS_OBJECT_NAME_NOT_FOUND");
                    return FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND;
                }
                else
                {
                    var D = FileNode.CreateFromWebDavObject(FoundElement, this.WebDAVMode);
                    if (SecurityDescriptor != null) SecurityDescriptor = D.FileSecurity;
                    FileAttributes = D.FileInfo.FileAttributes;
                    UpdateReservedFileNodeInCache(D);
                    DebugEnd(OperationId, "STATUS_SUCCESS - From Repository");
                    return STATUS_SUCCESS;
                }
            }
            else
            {
                FileAttributes = KnownNode.FileInfo.FileAttributes;
                if (null != SecurityDescriptor) SecurityDescriptor = KnownNode.FileSecurity;
                DebugEnd(OperationId, "STATUS_SUCCESS - From Cache");
                return STATUS_SUCCESS;
            }
        }

        public override Int32 Open(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            out Object FileNode0,
            out Object FileDesc,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, "", FileName);

            FileNode0 = default(Object);
            FileDesc = default(Object);
            FileInfo = default(FileInfo);
            NormalizedName = default(String);

            FileNode CFN = GetFileNodeFromCache(FileName, true);
            if (CFN == null)
            {
                WebDAVClient.Model.Item RepositoryObject;

                try
                {
                    RepositoryObject = GetRepositoryElement(FileName);
                    if (RepositoryObject == null)
                    {
                        RemoveReservedFileNodeFromCache(FileName);
                        DebugEnd(OperationId, "STATUS_OBJECT_NAME_NOT_FOUND");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }
                }
                catch (WebDAVException ex)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    DebugEnd(OperationId, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                    return STATUS_OBJECT_NAME_NOT_FOUND;
                }
                catch (HttpRequestException ex)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    RemoveReservedFileNodeFromCache(FileName);
                    DebugEnd(OperationId, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                    return STATUS_OBJECT_NAME_NOT_FOUND;
                }

                CFN = FileNode.CreateFromWebDavObject(RepositoryObject, this.WebDAVMode);
                UpdateReservedFileNodeInCache(CFN);
                Int32 i = Interlocked.Increment(ref CFN.OpenCount);
                DebugEnd(OperationId, $"STATUS_SUCCESS - From Repository - Handle {i}");
            }
            else
            {
                Int32 i = Interlocked.Increment(ref CFN.OpenCount);
                DebugEnd(OperationId, $"STATUS_SUCCESS - From cache - Handle {i}");
            }

            FileNode0 = CFN;
            FileInfo = CFN.FileInfo;
            NormalizedName = FileName;

            return STATUS_SUCCESS;
        }

        public override Int32 GetFileInfo(
            Object FileNode0,
            Object FileDesc,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;
            FileInfo = CFN.FileInfo;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);
            DebugEnd(OperationId, "STATUS_SUCCESS");

            return STATUS_SUCCESS;
        }

        public override Int32 GetSecurity(
            Object FileNode0,
            Object FileDesc,
            ref Byte[] SecurityDescriptor)
        {
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            SecurityDescriptor = CFN.FileSecurity;

            DebugEnd(OperationId, "STATUS_SUCCESS");

            return STATUS_SUCCESS;
        }

        public override Boolean ReadDirectoryEntry(
            Object FileNode0,
            Object FileDesc,
            String Pattern,
            String Marker, //Détermine à partir de quel fichier dans le répertoire, on commence à lire (exclusif) (null = start from the beginning)
            ref Object Context,
            out String FileName,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;
            IEnumerator<Tuple<String, FileNode>> Enumerator;
            String OperationId;

            if (Context == null)
            {
                LogTrace("Read directory start");

                Enumerator = null;
                OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                List<Tuple<String, FileNode>> ChildrenFileNames = null;
                var CachedFolder = GetFileNodeFromCache(CFN.LocalPath);

                if (CachedFolder == null || !CachedFolder.IsParsed)
                {
                    ChildrenFileNames = new List<Tuple<String, FileNode>>();

                    if (!IsRepositoryRootPath(CFN.RepositoryPath))
                    {
                        //if this is not the root directory add the dot entries
                        if (Marker == null) ChildrenFileNames.Add(new Tuple<String, FileNode>(".", CFN));

                        if (null == Marker || "." == Marker)
                        {
                            String ParentPath = FileNode.ConvertRepositoryPathToLocalPath(GetRepositoryParentPath(CFN.RepositoryPath));
                            if (ParentPath != null)
                            {
                                //RepositoryElement ParentElement;
                                try
                                {
                                    var ParentElement = GetRepositoryElement(ParentPath);
                                    if (ParentElement != null) ChildrenFileNames.Add(new Tuple<String, FileNode>("..", FileNode.CreateFromWebDavObject(ParentElement, this.WebDAVMode)));
                                }
                                catch { }
                            }
                        }
                    }

                    var Proxy = GenerateProxy();
                    IEnumerable<WebDAVClient.Model.Item> ItemsInFolder;

                    try
                    {
                        LogTrace("Read directory list start");
                        ItemsInFolder = Proxy.List(CFN.RepositoryPath).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        DebugEnd(OperationId, $"Exception : {ex.Message}");
                        FileName = default(String);
                        FileInfo = default(FileInfo);
                        return false;
                    }

                    LogTrace("Read directory list end");

                    foreach (var Children in ItemsInFolder)
                    {
                        var Element = FileNode.CreateFromWebDavObject(Children, this.WebDAVMode);
                        if (Element.RepositoryPath.Equals(CFN.RepositoryPath)) continue;
                        AddFileNodeToCache(Element);
                        ChildrenFileNames.Add(new Tuple<string, FileNode>(Element.Name, Element));
                    }

                    CFN.IsParsed = true;
                }
                else
                {
                    ChildrenFileNames = GetFolderContentFromCache(CFN.LocalPath).Select(x => new Tuple<String, FileNode>(x.Name, x)).ToList();
                }

                LogTrace("Read directory list transformed to FileNode");

                if (!String.IsNullOrEmpty(Marker)) //Dealing with potential marker
                {
                    var WantedTuple = ChildrenFileNames.FirstOrDefault(x => x.Item1.Equals(Marker));
                    var WantedTupleIndex = ChildrenFileNames.IndexOf(WantedTuple);
                    if (WantedTupleIndex + 1 < ChildrenFileNames.Count)
                    {
                        ChildrenFileNames = ChildrenFileNames.GetRange(WantedTupleIndex + 1, ChildrenFileNames.Count - 1 - WantedTupleIndex);
                    }
                    else
                    {
                        ChildrenFileNames.Clear();
                    }
                }

                Enumerator = ChildrenFileNames.GetEnumerator();
                Context = new DirectoryEnumeratorContext() { Enumerator = Enumerator, OperationId = OperationId };
            }
            else
            {
                Enumerator = ((DirectoryEnumeratorContext)Context).Enumerator;
                OperationId = ((DirectoryEnumeratorContext)Context).OperationId;
            }

            if (Enumerator.MoveNext())
            {
                LogTrace("Read directory enumerate");
                Tuple<String, FileNode> CurrentCFN = Enumerator.Current;
                FileName = CurrentCFN.Item1;
                FileInfo = CurrentCFN.Item2.FileInfo;
                return true;
            }

            DebugEnd(OperationId, "STATUS_SUCCESS");
            LogTrace("Read directory end");

            FileName = default(String);
            FileInfo = default(FileInfo);
            return false;
        }

        public override Int32 CanDelete(
            Object FileNode0,
            Object FileDesc,
            String FileName)
        {
            return STATUS_SUCCESS;
        }

        public override Int32 Create(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            UInt32 FileAttributes,
            Byte[] SecurityDescriptor,
            UInt64 AllocationSize,
            out Object FileNode0,
            out Object FileDesc,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, "", FileName);

            FileNode0 = default(Object);
            FileDesc = default(Object);
            FileInfo = default(FileInfo);
            NormalizedName = default(String);

            FileNode CFN;

            try
            {
                WebDAVClient.Model.Item KnownRepositoryElement = GetRepositoryElement(FileName);
                if (KnownRepositoryElement != null)
                {
                    DebugEnd(OperationId, "STATUS_OBJECT_NAME_COLLISION");
                    return STATUS_OBJECT_NAME_COLLISION;
                }
            }
            catch (HttpRequestException ex)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                return STATUS_NETWORK_UNREACHABLE;
            }
            catch (Exception ex)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                DebugEnd(OperationId, $"STATUS_CANNOT_MAKE - {ex.Message}");
                return FileSystemBase.STATUS_CANNOT_MAKE;
            }

            String NewDocumentName = Path.GetFileName(FileName);
            String NewDocumentParentPath = Path.GetDirectoryName(FileName);
            String RepositoryNewDocumentParentPath = ConvertLocalPathToRepositoryPath(NewDocumentParentPath);

            var Proxy = GenerateProxy();
            if ((FileAttributes & (UInt32)System.IO.FileAttributes.Directory) == 0)
            {
                try
                {
                    if (Proxy.Upload(RepositoryNewDocumentParentPath, new MemoryStream(new byte[0]), NewDocumentName).GetAwaiter().GetResult())
                    {
                        CFN = FileNode.CreateFromWebDavObject(GetRepositoryElement(FileName), this.WebDAVMode);
                    }
                    else
                    {
                        DebugEnd(OperationId, "STATUS_CANNOT_MAKE");
                        return STATUS_CANNOT_MAKE;
                    }
                }
                catch (WebDAVConflictException ex)
                {
                    //Seems that we get Conflict response when the folder cannot be created
                    //As we do conflict checking before running the CreateDir command, we consider it as a permission issue
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return STATUS_ACCESS_DENIED;
                }
                catch (HttpRequestException ex)
                {
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (WebDAVException ex)
                {
                    DebugEnd(OperationId, $"STATUS_CANNOT_MAKE - {ex.Message}");
                    return STATUS_CANNOT_MAKE;
                }
                catch (Exception ex)
                {
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return STATUS_ACCESS_DENIED;
                }
            }
            else
            {
                try
                {
                    if (Proxy.CreateDir(RepositoryNewDocumentParentPath, NewDocumentName).GetAwaiter().GetResult())
                    {
                        CFN = FileNode.CreateFromWebDavObject(GetRepositoryElement(FileName), this.WebDAVMode);
                    }
                    else
                    {
                        DebugEnd(OperationId, "STATUS_CANNOT_MAKE");
                        return STATUS_CANNOT_MAKE;
                    }
                }
                catch (WebDAVConflictException ex)
                {
                    //Seems that we get Conflict response when the folder cannot be created
                    //As we do conflict checking before running the CreateDir command, we consider it as a permission issue
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return STATUS_ACCESS_DENIED;
                }
                catch (WebDAVException ex)
                {
                    DebugEnd(OperationId, $"STATUS_CANNOT_MAKE - {ex.Message}");
                    return STATUS_CANNOT_MAKE;
                }
                catch (HttpRequestException ex)
                {
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return STATUS_ACCESS_DENIED;
                }
            }

            AddFileNodeToCache(CFN);

            Interlocked.Increment(ref CFN.OpenCount);
            FileNode0 = CFN;
            FileInfo = CFN.FileInfo;
            NormalizedName = FileName;

            DebugEnd(OperationId, $"STATUS_SUCCESS - Handle {CFN.OpenCount}");
            return STATUS_SUCCESS;

            /*
            FileNode FileNode;
            FileNode ParentNode;
            Int32 Result = STATUS_SUCCESS;

            FileNode = FileNodeMap.Get(FileName);
            if (null != FileNode)
                return STATUS_OBJECT_NAME_COLLISION;
            ParentNode = FileNodeMap.GetParent(FileName, ref Result);
            if (null == ParentNode)
                return Result;

            if (0 != (CreateOptions & FILE_DIRECTORY_FILE))
                AllocationSize = 0;
            if (FileNodeMap.Count() >= MaxFileNodes)
                return STATUS_CANNOT_MAKE;
            if (AllocationSize > MaxFileSize)
                return STATUS_DISK_FULL;

            if ("\\" != ParentNode.FileName) FileName = ParentNode.FileName + "\\" + Path.GetFileName(FileName); //normalize name
            FileNode = new FileNode(FileName);
            FileNode.MainFileNode = FileNodeMap.GetMain(FileName);
            FileNode.FileInfo.FileAttributes = 0 != (FileAttributes & (UInt32)System.IO.FileAttributes.Directory) ?
                FileAttributes : FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
            FileNode.FileSecurity = SecurityDescriptor;
            if (0 != AllocationSize)
            {
                Result = SetFileSizeInternal(FileNode, AllocationSize, true);
                if (0 > Result)
                    return Result;
            }
            FileNodeMap.Insert(FileNode);

            Interlocked.Increment(ref FileNode.OpenCount);
            FileNode0 = FileNode;
            FileInfo = FileNode.GetFileInfo();
            NormalizedName = FileNode.FileName;
            */
        }

        public override Int32 Overwrite(
            Object FileNode0,
            Object FileDesc,
            UInt32 FileAttributes,
            Boolean ReplaceFileAttributes,
            UInt64 AllocationSize,
            out FileInfo FileInfo)
        {
            FileInfo = default(FileInfo);
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            try
            {
                Int32 Result = SetFileSizeInternal(CFN, AllocationSize, true);
                if (Result < 0)
                {
                    DebugEnd(OperationId, Result.ToString());
                    return Result;
                }

                if (ReplaceFileAttributes) CFN.FileInfo.FileAttributes = FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                else CFN.FileInfo.FileAttributes |= FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                CFN.FileInfo.FileSize = 0;
                CFN.FileInfo.LastAccessTime =
                CFN.FileInfo.LastWriteTime =
                CFN.FileInfo.ChangeTime = (UInt64)DateTime.Now.ToFileTimeUtc();

                FileInfo = CFN.FileInfo;

                /*
                FileInfo = default(FileInfo);

                FileNode FileNode = (FileNode)FileNode0;
                Int32 Result;

                List<String> StreamFileNames = new List<String>(FileNodeMap.GetStreamFileNames(FileNode));
                foreach (String StreamFileName in StreamFileNames)
                {
                    FileNode StreamNode = FileNodeMap.Get(StreamFileName);
                    if (null == StreamNode)
                        continue; // should not happen
                    if (0 == StreamNode.OpenCount)
                        FileNodeMap.Remove(StreamNode);
                }

                Result = SetFileSizeInternal(FileNode, AllocationSize, true);
                if (0 > Result)
                    return Result;
                if (ReplaceFileAttributes)
                    FileNode.FileInfo.FileAttributes = FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                else
                    FileNode.FileInfo.FileAttributes |= FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                FileNode.FileInfo.FileSize = 0;
                FileNode.FileInfo.LastAccessTime =
                FileNode.FileInfo.LastWriteTime =
                FileNode.FileInfo.ChangeTime = (UInt64)DateTime.Now.ToFileTimeUtc();

                FileInfo = FileNode.GetFileInfo();
                */

                DebugEnd(OperationId, "STATUS_SUCCESS");
                return STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                DebugEnd(OperationId, $"Exception {ex.Message}");
                return STATUS_UNEXPECTED_IO_ERROR;
            }
        }

        public override void Close(
            Object FileNode0,
            Object FileDesc)
        {
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            Int32 HandleCount = Interlocked.Decrement(ref CFN.OpenCount);

            if (this.FlushMode == FlushMode.FlushAtCleanup)
            {
                if (CFN.HasUnflushedData)
                {
                    try
                    {
                        var Proxy = GenerateProxy();
                        if (!Proxy.Upload(GetRepositoryParentPath(CFN.RepositoryPath), new MemoryStream(CFN.FileData, 0, (int)CFN.FileInfo.FileSize), CFN.Name).GetAwaiter().GetResult())
                        {
                            throw new Exception("Upload failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugEnd(OperationId, ex.Message);
                        DeleteFileNodeFromCache(CFN);
                        
                        return;
                    }
                    CFN.HasUnflushedData = false;
                }
            }

            DebugEnd(OperationId, $"STATUS_SUCCESS - Handle 0");
        }

        public override void Cleanup(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            UInt32 Flags)
        {
            DateTime StartTime = DateTime.Now;
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            if (IsRepositoryRootPath(CFN.RepositoryPath)) return;

            if ((Flags & CleanupSetAllocationSize) != 0)
            {
                UInt64 AllocationUnit = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;
                UInt64 AllocationSize = (CFN.FileInfo.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
                SetFileSizeInternal(CFN, AllocationSize, true);
            }

            if ((Flags & CleanupDelete) != 0)
            {
                var Proxy = GenerateProxy();
                if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0)
                {
                    try
                    {
                        //Fichier
                        Proxy.DeleteFile(CFN.RepositoryPath).GetAwaiter().GetResult();
                        DeleteFileNodeFromCache(CFN);
                        DebugEnd(OperationId, "STATUS_SUCCESS - Delete");
                    }
                    catch (Exception ex)
                    {
                        DebugEnd(OperationId, $"Exception : {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        //Répertoire
                        Proxy.DeleteFolder(CFN.RepositoryPath).GetAwaiter().GetResult();
                        DeleteFileNodeFromCache(CFN);
                        DebugEnd(OperationId, "STATUS_SUCCESS - Delete");
                    }
                    catch (Exception ex)
                    {
                        DebugEnd(OperationId, $"Exception : {ex.Message}");
                    }
                }
            }
            else
            {
                if (this.FlushMode == FlushMode.FlushAtCleanup)
                {
                    if ((Flags & CleanupSetAllocationSize) != 0 || (Flags & CleanupSetArchiveBit) != 0 || (Flags & CleanupSetLastWriteTime) != 0)
                    {
                        if (CFN.HasUnflushedData)
                        {
                            var Proxy = GenerateProxy();
                            try
                            {
                                if (!Proxy.Upload(GetRepositoryParentPath(CFN.RepositoryPath), new MemoryStream(CFN.FileData, 0, (int)CFN.FileInfo.FileSize), CFN.Name).GetAwaiter().GetResult())
                                {
                                    throw new Exception("Upload failed");
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugEnd(OperationId, ex.Message);
                                //TODO : Remove from Cache
                                return;
                            }
                            CFN.HasUnflushedData = false;
                        }
                    }
                }
                DebugEnd(OperationId, "STATUS_SUCCESS");
            }

            /*
            FileNode FileNode = (FileNode)FileNode0;

            FileNode MainFileNode = null != FileNode.MainFileNode ?
                FileNode.MainFileNode : FileNode;

            if (0 != (Flags & CleanupSetArchiveBit))
            {
                if (0 == (MainFileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    MainFileNode.FileInfo.FileAttributes |= (UInt32)FileAttributes.Archive;
            }

            if (0 != (Flags & (CleanupSetLastAccessTime | CleanupSetLastWriteTime | CleanupSetChangeTime)))
            {
                UInt64 SystemTime = (UInt64)DateTime.Now.ToFileTimeUtc();

                if (0 != (Flags & CleanupSetLastAccessTime))
                    MainFileNode.FileInfo.LastAccessTime = SystemTime;
                if (0 != (Flags & CleanupSetLastWriteTime))
                    MainFileNode.FileInfo.LastWriteTime = SystemTime;
                if (0 != (Flags & CleanupSetChangeTime))
                    MainFileNode.FileInfo.ChangeTime = SystemTime;
            }

            if (0 != (Flags & CleanupSetAllocationSize))
            {
                UInt64 AllocationUnit = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;
                UInt64 AllocationSize = (FileNode.FileInfo.FileSize + AllocationUnit - 1) /
                    AllocationUnit * AllocationUnit;
                SetFileSizeInternal(FileNode, AllocationSize, true);
            }

            if (0 != (Flags & CleanupDelete) && !FileNodeMap.HasChild(FileNode))
            {
                List<String> StreamFileNames = new List<String>(FileNodeMap.GetStreamFileNames(FileNode));
                foreach (String StreamFileName in StreamFileNames)
                {
                    FileNode StreamNode = FileNodeMap.Get(StreamFileName);
                    if (null == StreamNode)
                        continue; // should not happen
                    FileNodeMap.Remove(StreamNode);
                }
                FileNodeMap.Remove(FileNode);
            }
            */
        }

        public override Int32 Read(
            Object FileNode0,
            Object FileDesc,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            out UInt32 BytesTransferred)
        {
            BytesTransferred = default(UInt32);
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            if (CFN.FileData == null)
            {
                var Proxy = GenerateProxy();
                try
                {
                    CFN.FileData = Proxy.Download(CFN.RepositoryPath).GetAwaiter().GetResult();
                }
                catch (HttpRequestException ex)
                {
                    CFN.FileData = null;
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    CFN.FileData = null;
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
            }

            if (CFN.FileData == null)
            {
                DebugEnd(OperationId, "STATUS_OBJECT_NAME_NOT_FOUND");
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            UInt64 FileSize = (UInt64)CFN.FileData.LongLength;

            if (Offset >= FileSize)
            {
                BytesTransferred = default(UInt32);
                DebugEnd(OperationId, "STATUS_END_OF_FILE");
                return STATUS_END_OF_FILE;
            }

            UInt64 EndOffset = Offset + Length;
            if (EndOffset > FileSize) EndOffset = FileSize;

            BytesTransferred = (UInt32)(EndOffset - Offset);
            Marshal.Copy(CFN.FileData, (int)Offset, Buffer, (int)BytesTransferred);

            /*
            FileNode FileNode = (FileNode)FileNode0;
            UInt64 EndOffset;

            if (Offset >= FileNode.FileInfo.FileSize)
            {
                BytesTransferred = default(UInt32);
                return STATUS_END_OF_FILE;
            }

            EndOffset = Offset + Length;
            if (EndOffset > FileNode.FileInfo.FileSize)
                EndOffset = FileNode.FileInfo.FileSize;

            BytesTransferred = (UInt32)(EndOffset - Offset);
            Marshal.Copy(FileNode.FileData, (int)Offset, Buffer, (int)BytesTransferred);
            */

            //LogSuccess($"{CFN.handle} Read {CFN.LocalPath} from {Offset} for {BytesTransferred} bytes | requested {Length} bytes");
            DebugEnd(OperationId, "STATUS_SUCCESS");
            return STATUS_SUCCESS;
        }

        public override Int32 Write(
            Object FileNode0,
            Object FileDesc,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            Boolean WriteToEndOfFile,
            Boolean ConstrainedIo,
            out UInt32 BytesTransferred,
            out FileInfo FileInfo)
        {
            DateTime StartTime = DateTime.Now;
            BytesTransferred = default(UInt32);
            FileInfo = default(FileInfo);
            UInt64 EndOffset;

            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            /*
            CFN.FileData = null;
            byte[] RepositoryContent = CFN.GetContent(Proxy);
            CFN.FileData = new byte[(int)CFN.FileInfo.FileSize];
            if (CFN.FileData == null)
            {
                BytesTransferred = default(UInt32);
                FileInfo = default(FileInfo);
                return STATUS_SUCCESS;
            }
            try
            {
                Array.Copy(RepositoryContent, CFN.FileData, Math.Min((int)CFN.FileInfo.FileSize, RepositoryContent.Length));
            }
            catch (Exception ex)
            {
                LogTrace($"{CFN.handle} Write Exception {ex.Message}");
            }
            */

            if (ConstrainedIo)
            {
                //ContrainedIo - we cannot increase the file size so EndOffset will always be at maximum equal to CFN.FileInfo.FileSize
                if (Offset >= CFN.FileInfo.FileSize)
                {
                    LogTrace($"{CFN.handle} ***Write*** {CFN.Name} [{Path.GetFileName(CFN.Name)}] Case 1");
                    BytesTransferred = default(UInt32);
                    FileInfo = default(FileInfo);
                    DebugEnd(OperationId, "STATUS_SUCCESS");
                    return STATUS_SUCCESS;
                }

                EndOffset = Offset + Length;
                if (EndOffset > CFN.FileInfo.FileSize) EndOffset = CFN.FileInfo.FileSize;
            }
            else
            {
                if (WriteToEndOfFile) Offset = CFN.FileInfo.FileSize; //We write at the end the file so whatever the Offset is, we set it to be equal to the file size

                EndOffset = Offset + Length;

                if (EndOffset > CFN.FileInfo.FileSize) //We are not in a ConstrainedIo so we expand the file size if the EndOffset goes beyond the current file size
                {
                    LogTrace($"{CFN.handle} Write Increase FileSize {CFN.Name}");
                    Int32 Result = SetFileSizeInternal(CFN, EndOffset, false);
                    if (Result < 0)
                    {
                        LogTrace($"{CFN.handle} ***Write*** {CFN.Name} [{Path.GetFileName(CFN.Name)}] Case 2");
                        BytesTransferred = default(UInt32);
                        FileInfo = default(FileInfo);
                        DebugEnd(OperationId, Result.ToString());
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }
                }
            }

            BytesTransferred = (UInt32)(EndOffset - Offset);
            try
            {
                Marshal.Copy(Buffer, CFN.FileData, (int)Offset, (int)BytesTransferred);
            }
            catch (Exception ex)
            {
                LogTrace($"{CFN.handle} Write Exception {ex.Message}");
                BytesTransferred = default(UInt32);
                FileInfo = default(FileInfo);
                DebugEnd(OperationId, "-1");
                return STATUS_UNEXPECTED_IO_ERROR;
            }

            if (this.FlushMode == FlushMode.FlushAtWrite)
            {
                var Proxy = GenerateProxy();
                try
                {
                    if (!Proxy.Upload(GetRepositoryParentPath(CFN.RepositoryPath), new MemoryStream(CFN.FileData.Take((int)CFN.FileInfo.FileSize).ToArray()), CFN.Name).GetAwaiter().GetResult())
                    {
                        throw new Exception();
                    }
                }
                catch (WebDAVConflictException ex)
                {
                    InvalidateFileNodeInCache(CFN);
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return STATUS_ACCESS_DENIED;
                }
                catch (HttpRequestException ex)
                {
                    InvalidateFileNodeInCache(CFN);
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    InvalidateFileNodeInCache(CFN);
                    DebugEnd(OperationId, $"STATUS_UNEXPECTED_IO_ERROR - {ex.Message}");
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            else
            {
                CFN.HasUnflushedData = true;
            }
            FileInfo = CFN.FileInfo;

            LogTrace($"{CFN.handle} Write {CFN.RepositoryPath} at {Offset} for {BytesTransferred} bytes | Requested {Length} bytes | {ConstrainedIo}");
            DebugEnd(OperationId, "STATUS_SUCCESS");
            return STATUS_SUCCESS;

            /*
            FileNode FileNode = (FileNode)FileNode0;
            UInt64 EndOffset;

            if (ConstrainedIo)
            {
                if (Offset >= FileNode.FileInfo.FileSize)
                {
                    BytesTransferred = default(UInt32);
                    FileInfo = default(FileInfo);
                    return STATUS_SUCCESS;
                }
                EndOffset = Offset + Length;
                if (EndOffset > FileNode.FileInfo.FileSize)
                    EndOffset = FileNode.FileInfo.FileSize;
            }
            else
            {
                if (WriteToEndOfFile)
                    Offset = FileNode.FileInfo.FileSize;
                EndOffset = Offset + Length;
                if (EndOffset > FileNode.FileInfo.FileSize)
                {
                    Int32 Result = SetFileSizeInternal(FileNode, EndOffset, false);
                    if (0 > Result)
                    {
                        BytesTransferred = default(UInt32);
                        FileInfo = default(FileInfo);
                        return Result;
                    }
                }
            }

            BytesTransferred = (UInt32)(EndOffset - Offset);
            Marshal.Copy(Buffer, FileNode.FileData, (int)Offset, (int)BytesTransferred);

            FileInfo = FileNode.GetFileInfo();
            */
        }

        public override Int32 Rename(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            String NewFileName,
            Boolean ReplaceIfExists)
        {
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            if (FileName == NewFileName) return STATUS_SUCCESS;

            String RepositoryDocumentName = ConvertLocalPathToRepositoryPath(FileName);
            String RepositoryTargetDocumentName = ConvertLocalPathToRepositoryPath(NewFileName);

            try
            {
                WebDAVClient.Model.Item KnownRepositoryElement = GetRepositoryElement(NewFileName);
                if (KnownRepositoryElement != null && !ReplaceIfExists)
                {
                    DebugEnd(OperationId, $"STATUS_OBJECT_NAME_COLLISION");
                    return STATUS_OBJECT_NAME_COLLISION;
                }
            }
            catch (HttpRequestException ex)
            {
                DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                return STATUS_NETWORK_UNREACHABLE;
            }
            catch (Exception ex)
            {
                DebugEnd(OperationId, $"STATUS_CANNOT_MAKE - {ex.Message}");
                return FileSystemBase.STATUS_CANNOT_MAKE;
            }

            var Proxy = GenerateProxy();
            if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0)
            {
                //Fichier
                try
                {
                    if (!Proxy.MoveFile(RepositoryDocumentName, RepositoryTargetDocumentName).GetAwaiter().GetResult())
                    {
                        DebugEnd(OperationId, "STATUS_ACCESS_DENIED");
                        return STATUS_ACCESS_DENIED;
                    }
                }
                catch (HttpRequestException ex)
                {
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return FileSystemBase.STATUS_ACCESS_DENIED;
                }
            }
            else
            {
                //Répertoire
                try
                {
                    if (!Proxy.MoveFolder(RepositoryDocumentName, RepositoryTargetDocumentName).GetAwaiter().GetResult())
                    {
                        DebugEnd(OperationId, "STATUS_ACCESS_DENIED");
                        return STATUS_ACCESS_DENIED;
                    }

                }
                catch (HttpRequestException ex)
                {
                    DebugEnd(OperationId, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    DebugEnd(OperationId, $"STATUS_ACCESS_DENIED - {ex.Message}");
                    return FileSystemBase.STATUS_ACCESS_DENIED;
                }
            }

            CFN.RepositoryPath = RepositoryTargetDocumentName;
            CFN.Name = GetRepositoryDocumentName(RepositoryTargetDocumentName);
            CFN.LocalPath = NewFileName;

            if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0)
            {
                RenameFolderSubElementsInCache(FileName, NewFileName);
            }

            /*
            FileNode FileNode = (FileNode)FileNode0;
            FileNode NewFileNode;

            NewFileNode = FileNodeMap.Get(NewFileName);
            if (null != NewFileNode && FileNode != NewFileNode)
            {
                if (!ReplaceIfExists)
                    return STATUS_OBJECT_NAME_COLLISION;
                if (0 != (NewFileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    return STATUS_ACCESS_DENIED;
            }

            if (null != NewFileNode && FileNode != NewFileNode)
                FileNodeMap.Remove(NewFileNode);

            List<String> DescendantFileNames = new List<String>(FileNodeMap.GetDescendantFileNames(FileNode));
            foreach (String DescendantFileName in DescendantFileNames)
            {
                FileNode DescendantFileNode = FileNodeMap.Get(DescendantFileName);
                if (null == DescendantFileNode)
                    continue; // should not happen
                FileNodeMap.Remove(DescendantFileNode);
                DescendantFileNode.FileName =
                    NewFileName + DescendantFileNode.FileName.Substring(FileName.Length);
                FileNodeMap.Insert(DescendantFileNode);
            }
            */
            DebugEnd(OperationId, $"STATUS_SUCCESS. Renamed to {NewFileName}");
            return STATUS_SUCCESS;
        }

        public override Int32 Flush(
            Object FileNode0,
            Object FileDesc,
            out FileInfo FileInfo)
        {
            DateTime StartTime = DateTime.Now;
            FileNode CFN = (FileNode)FileNode0;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            FileInfo = (null != CFN ? CFN.FileInfo : default(FileInfo));

            DebugEnd(OperationId, "STATUS_SUCCESS");

            return STATUS_SUCCESS;
        }

        public override Int32 SetBasicInfo(
            Object FileNode0,
            Object FileDesc,
            UInt32 FileAttributes,
            UInt64 CreationTime,
            UInt64 LastAccessTime,
            UInt64 LastWriteTime,
            UInt64 ChangeTime,
            out FileInfo FileInfo)
        {
            DateTime StartTime = DateTime.Now;
            FileNode CFN = (FileNode)FileNode0;

            if (unchecked((UInt32)(-1)) != FileAttributes)
                CFN.FileInfo.FileAttributes = FileAttributes;
            if (0 != CreationTime)
                CFN.FileInfo.CreationTime = CreationTime;
            if (0 != LastAccessTime)
                CFN.FileInfo.LastAccessTime = LastAccessTime;
            if (0 != LastWriteTime)
                CFN.FileInfo.LastWriteTime = LastWriteTime;
            if (0 != ChangeTime)
                CFN.FileInfo.ChangeTime = ChangeTime;

            FileInfo = CFN.FileInfo;

            //TODO : We cannot override those informations in Alfresco, so ... what ?
            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (unchecked((UInt32)(-1)) != FileAttributes)
                FileNode.FileInfo.FileAttributes = FileAttributes;
            if (0 != CreationTime)
                FileNode.FileInfo.CreationTime = CreationTime;
            if (0 != LastAccessTime)
                FileNode.FileInfo.LastAccessTime = LastAccessTime;
            if (0 != LastWriteTime)
                FileNode.FileInfo.LastWriteTime = LastWriteTime;
            if (0 != ChangeTime)
                FileNode.FileInfo.ChangeTime = ChangeTime;

            FileInfo = FileNode.GetFileInfo();
            */
            //Console.WriteLine($"{CFN.handle} SetBasicInfo {CFN.FullPath} in {(DateTime.Now - StartTime).TotalSeconds}");
            return STATUS_SUCCESS;
        }

        public override Int32 SetFileSize(
            Object FileNode0,
            Object FileDesc,
            UInt64 NewSize,
            Boolean SetAllocationSize,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;
            FileInfo = CFN.FileInfo;

            Int32 Result = SetFileSizeInternal(CFN, NewSize, SetAllocationSize);
            FileInfo = Result >= 0 ? CFN.FileInfo : default(FileInfo);

            LogTrace($"{CFN.handle} SetFileSize File {CFN.LocalPath}. New Size {NewSize}. Allocation size {SetAllocationSize}");

            /*
            FileNode FileNode = (FileNode)FileNode0;
            Int32 Result;

            Result = SetFileSizeInternal(FileNode, NewSize, SetAllocationSize);
            FileInfo = 0 <= Result ? FileNode.GetFileInfo() : default(FileInfo);
            */

            return STATUS_SUCCESS;
        }

        private Int32 SetFileSizeInternal(
            FileNode FileNode,
            UInt64 NewSize,
            Boolean SetAllocationSize)
        {
            try
            {
                if (SetAllocationSize)
                {
                    if (FileNode.FileInfo.AllocationSize != NewSize)
                    {
                        if (NewSize > MaxFileSize) return STATUS_DISK_FULL;

                        byte[] FileData = null;
                        if (NewSize != 0)
                        {
                            try
                            {
                                FileData = new byte[NewSize];
                            }
                            catch
                            {
                                return STATUS_INSUFFICIENT_RESOURCES;
                            }
                        }
                        int CopyLength = (int)Math.Min(FileNode.FileInfo.AllocationSize, NewSize);
                        if (CopyLength != 0) Array.Copy(FileNode.FileData, FileData, CopyLength);

                        FileNode.FileData = FileData;
                        FileNode.FileInfo.AllocationSize = NewSize;
                        if (FileNode.FileInfo.FileSize > NewSize) FileNode.FileInfo.FileSize = NewSize;
                    }
                }
                else
                {
                    if (FileNode.FileInfo.FileSize != NewSize)
                    {
                        if (FileNode.FileInfo.AllocationSize < NewSize)
                        {
                            UInt64 AllocationUnit = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;
                            UInt64 AllocationSize = (NewSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
                            Int32 Result = SetFileSizeInternal(FileNode, AllocationSize, true);
                            if (Result < 0) return Result;
                        }

                        if (NewSize > FileNode.FileInfo.FileSize)
                        {
                            int CopyLength = (int)(NewSize - FileNode.FileInfo.FileSize);
                            if (CopyLength != 0) Array.Clear(FileNode.FileData, (int)FileNode.FileInfo.FileSize, CopyLength);
                        }

                        FileNode.FileInfo.FileSize = NewSize;
                    }
                }
            }
            catch (Exception ex)
            {
                LogTrace($"{FileNode.handle} SetFileSizeInternal {ex.Message} {ex.StackTrace}");
            }

            return STATUS_SUCCESS;
        }

        public override Int32 SetSecurity(
            Object FileNode0,
            Object FileDesc,
            AccessControlSections Sections,
            Byte[] SecurityDescriptor)
        {
            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            FileNode.FileSecurity = ModifySecurityDescriptor(
                FileNode.FileSecurity, Sections, SecurityDescriptor);
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        public override Boolean GetStreamEntry(
            Object FileNode0,
            Object FileDesc,
            ref Object Context,
            out String StreamName,
            out UInt64 StreamSize,
            out UInt64 StreamAllocationSize)
        {
            LogTrace("Not implemented : GetStreamEntry");

            /*
            FileNode FileNode = (FileNode)FileNode0;
            IEnumerator<String> Enumerator = (IEnumerator<String>)Context;

            if (null == Enumerator)
            {
                if (null != FileNode.MainFileNode)
                    FileNode = FileNode.MainFileNode;

                List<String> StreamFileNames = new List<String>();
                if (0 == (FileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    StreamFileNames.Add(FileNode.FileName);
                StreamFileNames.AddRange(FileNodeMap.GetStreamFileNames(FileNode));
                Context = Enumerator = StreamFileNames.GetEnumerator();
            }

            while (Enumerator.MoveNext())
            {
                String FullFileName = Enumerator.Current;
                FileNode StreamFileNode = FileNodeMap.Get(FullFileName);
                if (null != StreamFileNode)
                {
                    int Index = FullFileName.IndexOf(':');
                    if (0 > Index)
                        StreamName = "";
                    else
                        StreamName = FullFileName.Substring(Index + 1);
                    StreamSize = StreamFileNode.FileInfo.FileSize;
                    StreamAllocationSize = StreamFileNode.FileInfo.AllocationSize;
                    return true;
                }
            }
            */
            StreamName = default(String);
            StreamSize = default(UInt64);
            StreamAllocationSize = default(UInt64);
            return false;
        }

        #region Reparse points

        public override Int32 GetReparsePointByName(
            String FileName,
            Boolean IsDirectory,
            ref Byte[] ReparseData)
        {
            LogTrace("Not implemented : GetReparsePointByName");

            /*
            FileNode FileNode;

            FileNode = FileNodeMap.Get(FileName);
            if (null == FileNode)
                return STATUS_OBJECT_NAME_NOT_FOUND;

            if (0 == (FileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.ReparsePoint))
                return STATUS_NOT_A_REPARSE_POINT;

            ReparseData = FileNode.ReparseData;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        public override Int32 GetReparsePoint(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            ref Byte[] ReparseData)
        {
            LogTrace("Not implemented : GetReparsePoint");

            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (0 == (FileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.ReparsePoint))
                return STATUS_NOT_A_REPARSE_POINT;

            ReparseData = FileNode.ReparseData;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        public override Int32 SetReparsePoint(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            Byte[] ReparseData)
        {
            LogTrace("Not implemented : SetReparsePoint");

            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (FileNodeMap.HasChild(FileNode))
                return STATUS_DIRECTORY_NOT_EMPTY;

            if (null != FileNode.ReparseData)
            {
                Int32 Result = CanReplaceReparsePoint(FileNode.ReparseData, ReparseData);
                if (0 > Result)
                    return Result;
            }

            FileNode.FileInfo.FileAttributes |= (UInt32)FileAttributes.ReparsePoint;
            FileNode.FileInfo.ReparseTag = GetReparseTag(ReparseData);
            FileNode.ReparseData = ReparseData;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        public override Int32 DeleteReparsePoint(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            Byte[] ReparseData)
        {
            LogTrace("Not implemented : DeleteReparsePoint");

            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (null != FileNode.ReparseData)
            {
                Int32 Result = CanReplaceReparsePoint(FileNode.ReparseData, ReparseData);
                if (0 > Result)
                    return Result;
            }
            else
                return STATUS_NOT_A_REPARSE_POINT;

            FileNode.FileInfo.FileAttributes &= ~(UInt32)FileAttributes.ReparsePoint;
            FileNode.FileInfo.ReparseTag = 0;
            FileNode.ReparseData = null;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        #endregion

        #region Tools

        public String ConvertLocalPathToRepositoryPath(String LocalPath)
        {
            String ReworkdPath = DocumentLibraryPath + LocalPath.Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (ReworkdPath.EndsWith("/")) ReworkdPath = ReworkdPath.Substring(0, ReworkdPath.Length - 1);
            return ReworkdPath;
        }

        private bool IsRepositoryRootPath(String RepositoryPath)
        {
            return RepositoryPath.Equals(DocumentLibraryPath);
        }

        public String GetRepositoryDocumentName(String DocumentPath)
        {
            if (DocumentPath.EndsWith("/")) DocumentPath = DocumentPath.Substring(0, DocumentPath.Length - 1);

            if (DocumentPath.Equals(DocumentLibraryPath)) return "\\";
            return DocumentPath.Substring(DocumentPath.LastIndexOf('/') + 1);
        }

        public String GetRepositoryParentPath(String DocumentPath)
        {
            if (DocumentPath.EndsWith("/")) DocumentPath = DocumentPath.Substring(0, DocumentPath.Length - 1);

            if (DocumentPath.Equals(DocumentLibraryPath)) return null;
            if (DocumentPath.Length < DocumentLibraryPath.Length) return null;
            return DocumentPath.Substring(0, DocumentPath.LastIndexOf('/'));
        }

        private WebDavClient2 GenerateProxy()
        {
            return new WebDavClient2(this.WebDAVMode, this.DAVServer, this.DocumentLibraryPath, this.DAVLogin, this.DAVPassword);
        }

        private static object loglock = new object();

        public static void LogTrace(String Message)
        {
            lock (loglock)
            {
                logger.Trace(Message);
            }
        }

        #endregion

        public override void Unmounted(object Host)
        {
            FileNodeCache.Clear();
            FileNodeCache = null;
            base.Unmounted(Host);
        }

        private void DebugStart(string OperationId, FileNode CFN, [CallerMemberName]string Caller = "")
        {
            DebugMessageQueue.Enqueue(new DebugMessage() { MessageType = 0, date = DateTime.Now, Handle = CFN.handle, OperationId = OperationId, Path = CFN.LocalPath, Caller = Caller });
            DebugMessagePosted?.Invoke(null, null);
            //DebugMessagePosted?.BeginInvoke(this, null, DebugMessagePostedEndAsync, null);
        }

        private void DebugStart(String OperationId, String Handle, String FileName, [CallerMemberName]string Caller = "")
        {
            DebugMessageQueue.Enqueue(new DebugMessage() { MessageType = 0, date = DateTime.Now, Handle = Handle, OperationId = OperationId, Path = FileName, Caller = Caller });
            //DebugMessagePosted?.BeginInvoke(this, null, DebugMessagePostedEndAsync, null);
            DebugMessagePosted?.Invoke(null, null);
        }

        private void DebugEnd(String OperationId, String Result)
        {
            DebugMessageQueue.Enqueue(new DebugMessage() { MessageType = 1, date = DateTime.Now, OperationId = OperationId, Result = Result });
            //DebugMessagePosted?.BeginInvoke(this, null, DebugMessagePostedEndAsync, null);
            DebugMessagePosted?.Invoke(null, null);
        }

        //private void DebugMessagePostedEndAsync(IAsyncResult iar)
        //{
        //    var ar = (System.Runtime.Remoting.Messaging.AsyncResult)iar;
        //    var invokedMethod = (EventHandler)ar.AsyncDelegate;

        //    try
        //    {
        //        invokedMethod.EndInvoke(iar);
        //    }
        //    catch
        //    {
        //    }
        //}
    }
}