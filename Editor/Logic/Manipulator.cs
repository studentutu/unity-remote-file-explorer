using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RemoteFileExplorer.Editor.UI;
using UnityEditor;
using System.IO;
using System;

namespace RemoteFileExplorer.Editor
{
    public class Manipulator
    {
        public string m_CurPath;

        public string curPath
        {
            get
            {
                return m_CurPath;
            }
            set
            {
                m_CurPath = FileUtil.FixedPath(value);
            }
        }

        private RemoteFileExplorerWindow m_Owner;
        public Manipulator(RemoteFileExplorerWindow owner)
        {
            m_Owner = owner;
        }

        public void Refresh()
        {
            if(string.IsNullOrEmpty(curPath)) return;
            GoTo(curPath);
        }

        public void GoTo(ObjectItem item)
        {
            var data = item.Data;
            if (data.type == ObjectType.File)
                return;
            GoTo(data.path);
        }

        public void GoTo(string path)
        {
            Coroutines.Start(Internal_GoTo(path));
        }

        public void GoToByKey(string key)
        {
            Coroutines.Start(Internal_GoTo(key, true));
        }

        public void Select(ObjectItem item)
        {
            var data = item.Data;
            curPath = data.path;
            m_Owner.m_ObjectListArea.SetSelectItem(item);
        }

        /// <summary>
        /// 选择空
        /// </summary>
        public void Select()
        {
            ObjectItem item = m_Owner.m_ObjectListArea.GetSelectItem();
            if (item != null)
            {
                curPath = Path.GetDirectoryName(item.Data.path);
            }
            m_Owner.m_ObjectListArea.SetSelectItem(null);
        }

        public void Download(ObjectItem item)
        {
            var data = item.Data;
            string path = data.path;
            string dest = null;
            if(data.type == ObjectType.File)
            {
                dest = EditorUtility.SaveFilePanel(Constants.SelectFileTitle, "", Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
            }
            else
            {
                dest = EditorUtility.SaveFolderPanel(Constants.SelectFileTitle, "", "");
                dest = FileUtil.CombinePath(dest, Path.GetFileName(path));
            }
            if (!string.IsNullOrEmpty(dest))
            {
                Coroutines.Start(Internal_Download(path, dest));
            }
        }

        public void Delete(ObjectItem item)
        {

        }

        public void Rename(ObjectItem item)
        {

        }

        public void UploadFile()
        {
            string path = EditorUtility.OpenFilePanel(Constants.SelectFileTitle, "", "");
            if (!string.IsNullOrEmpty(path))
            {
                Upload(new string[] { path });
            }
        }

        public void UploadFolder()
        {
            string path = EditorUtility.OpenFolderPanel(Constants.SelectFolderTitle, "", "");
            if (!string.IsNullOrEmpty(path))
            {
                Upload(new string[] { path });
            }
        }

        public void Upload(string[] paths)
        {
            string dest = curPath;
            ObjectItem item = m_Owner.m_ObjectListArea.GetSelectItem();
            if (item != null)
            {
                dest = Path.GetDirectoryName(item.Data.path);
            }
            if (string.IsNullOrEmpty(dest))
            {
                EditorUtility.DisplayDialog(Constants.WindowTitle, Constants.NoDestPathTip, Constants.OkText);
                return;
            }
            foreach (string path in paths)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    EditorUtility.DisplayDialog(Constants.WindowTitle, string.Format(Constants.PathNotExistTip, path), Constants.OkText);
                    return;
                }
            }
            Coroutines.Start(Internal_Upload(paths, dest));
        }

        /// <summary>
        /// 跳转到指定路径
        /// </summary>
        private IEnumerator Internal_GoTo(string path, bool isKey = false)
        {
            if (!CheckConnectStatus()) yield break;
            Command req;
            if (isKey)
            {
                req = new QueryPathKeyInfo.Req
                {
                    PathKey = path,
                };
            }
            else
            {
                req = new QueryPathInfo.Req
                {
                    Path = path,
                };
            }
            CommandHandle handle = m_Owner.m_Server.Send(req);
            yield return handle;
            if (!CheckHandleError(handle) || !CheckCommandError(handle.Command))
            {
                yield break;
            }
            var rsp = handle.Command as QueryPathInfo.Rsp;
            if (!rsp.Exists)
            {
                EditorUtility.DisplayDialog(Constants.WindowTitle, string.Format(Constants.PathNotExistTip, path), Constants.OkText);
                yield break;
            }
            List<ObjectData> list = new List<ObjectData>();

            foreach (var item1 in rsp.Directories)
            {
                list.Add(new ObjectData(ObjectType.Folder, item1));
            }
            foreach (var item1 in rsp.Files)
            {
                list.Add(new ObjectData(ObjectType.File, item1));
            }
            m_Owner.m_ObjectListArea.UpdateView(list);
            if (rsp is QueryPathKeyInfo.Rsp)
            {
                curPath = (rsp as QueryPathKeyInfo.Rsp).Path;
            }
            else
            {
                curPath = path;
            }
        }

        /// <summary>
        /// 下载
        /// </summary>
        private IEnumerator Internal_Download(string path, string dest)
        {
            if (!CheckConnectStatus()) yield break;
            var req = new Pull.Req
            {
                Path = path,
            };
            CommandHandle handle = m_Owner.m_Server.Send(req);
            yield return handle;
            string downloadFailedTip = string.Format(Constants.DownloadFailedTip, path);
            while (CheckHandleError(handle, downloadFailedTip) && CheckCommandError(handle.Command, downloadFailedTip))
            {
                if (handle.Command is CreateDirectory.Req)
                {
                    
                    var createDirectoryReq = handle.Command as CreateDirectory.Req;
                    CreateDirectory.Rsp rsp = new CreateDirectory.Rsp()
                    {
                        Ack = createDirectoryReq.Seq,
                    };
                    try
                    {
                        foreach (string directory in ConvertPaths(path, dest, createDirectoryReq.Directories))
                        {
                            if(!Directory.Exists(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        rsp.Error = e.Message;
                    }
                    m_Owner.m_Server.Send(rsp);
                    if (!CheckCommandError(rsp, downloadFailedTip))
                    {
                        yield break;
                    }
                    handle.Finished = false;
                    yield return handle;
                }
                else if (handle.Command is TransferFile.Req)
                {
                    var transferFileReq = handle.Command as TransferFile.Req;
                    TransferFile.Rsp rsp = new TransferFile.Rsp()
                    {
                        Ack = transferFileReq.Seq,
                    };
                    try
                    {
                        File.WriteAllBytes(ConvertPath(path, dest, transferFileReq.Path), transferFileReq.Content);
                    }
                    catch (Exception e)
                    {
                        rsp.Error = e.Message;
                    }
                    m_Owner.m_Server.Send(rsp);
                    if (!CheckCommandError(rsp, downloadFailedTip))
                    {
                        yield break;
                    }
                    handle.Finished = false;
                    yield return handle;
                }
                else if (handle.Command is Pull.Rsp)
                {
                    EditorUtility.DisplayDialog(Constants.WindowTitle, string.Format(Constants.DownloadSuccessTip, path), Constants.OkText);
                    yield break;
                }
                else
                {
                    EditorUtility.DisplayDialog(Constants.WindowTitle, downloadFailedTip + Constants.UnknownError, Constants.OkText);
                    yield break;
                }
            }
        }

        private IEnumerator Internal_Upload(string[] paths, string dest)
        {
            if (!CheckConnectStatus()) yield break;
            string uploadConfirmTip = string.Format(Constants.UploadConfirmTip, "\n", string.Join("\n", paths), dest);
            bool ret = EditorUtility.DisplayDialog(Constants.WindowTitle, uploadConfirmTip, Constants.OkText, Constants.CancelText);
            if (!ret)
            {
                yield break;
            }
            foreach (string path in paths)
            {
                string error = null;
                string[] directories = null;
                string[] files = null;
                string curDest = FileUtil.CombinePath(dest, Path.GetFileName(path));  // dest一定是路径
                if (File.Exists(path))
                {
                    files = new string[] { path };
                }
                else
                {
                    directories = FileUtil.GetAllDirectories(path);
                    files = FileUtil.GetAllFiles(path);
                }
                if (directories != null)
                {
                    CreateDirectory.Req req = new CreateDirectory.Req()
                    {
                        Directories = ConvertPaths(path, curDest, directories),
                    };
                    CommandHandle handle = m_Owner.m_Server.Send(req);
                    yield return handle;
                    if (handle.Error != null)
                    {
                        error = handle.Error;
                    }
                    else if (!string.IsNullOrEmpty(handle.Command.Error))
                    {
                        error = handle.Command.Error;
                    }
                }
                if (error == null)
                {
                    foreach (string file in files)
                    {
                        byte[] content;
                        try
                        {
                            content = File.ReadAllBytes(file);
                        }
                        catch (Exception e)
                        {
                            error = e.Message;
                            break;
                        }
                        TransferFile.Req req = new TransferFile.Req()
                        {
                            Path = ConvertPath(path, curDest, file),
                            Content = content,
                        };
                        CommandHandle handle = m_Owner.m_Server.Send(req);
                        yield return handle;
                        if (handle.Error != null)
                        {
                            error = handle.Error;
                            break;
                        }
                        else if (!string.IsNullOrEmpty(handle.Command.Error))
                        {
                            error = handle.Command.Error;
                            break;
                        }
                    }
                }
                if (error == null)
                {
                    EditorUtility.DisplayDialog(Constants.WindowTitle, string.Format(Constants.UploadSuccessTip, path), Constants.OkText);
                }
                else
                {
                    EditorUtility.DisplayDialog(Constants.WindowTitle, string.Format(Constants.UploadFailedTip, path) + error, Constants.OkText);
                }
            }
        }

        public string[] ConvertPaths(string src, string dest, string[] curs)
        {
            string[] paths = new string[curs.Length];
            for (int i = 0; i < curs.Length; i++)
            {
                paths[i] = ConvertPath(src, dest, curs[i]);
            }
            return paths;
        }

        public string ConvertPath(string src, string dest, string cur)
        {
            src = FileUtil.FixedPath(src);
            if(src.EndsWith("/")) src = src.Substring(0, src.Length - 1);
            dest = FileUtil.FixedPath(dest);
            cur = FileUtil.FixedPath(cur);
            return dest + cur.Replace(src, "");
        }

        public bool CheckConnectStatus()
        {
            if (m_Owner.m_Server.Status == ConnectStatus.Connected)
            {
                return true;
            }
            EditorUtility.DisplayDialog(Constants.WindowTitle, Constants.NotConnectedTip, Constants.OkText);
            return false;
        }

        public bool CheckHandleError(CommandHandle handle, string tip = "")
        {
            if (handle.Error != null)
            {
                EditorUtility.DisplayDialog(Constants.WindowTitle, tip + "handle.Error", Constants.OkText);
                return false;
            }
            return true;
        }

        public bool CheckCommandError(Command command, string tip = "")
        {
            if (!string.IsNullOrEmpty(command.Error))
            {
                EditorUtility.DisplayDialog(Constants.WindowTitle, tip + command.Error, Constants.OkText);
                return false;
            }
            return true;
        }
    }
}