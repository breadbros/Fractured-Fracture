﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
#if WINDOWS
#endif

#if !WINDOWS && !SDL2 && !FNA
namespace System.IO {
    // Who would ever want to throw exceptions on the XBox? Apparently more people than they thought
    public sealed class InvalidDataException : SystemException {
        public InvalidDataException ()
            : base() {
        }

        public InvalidDataException (string message)
            : base(message) {
        }

        public InvalidDataException (string message, Exception innerException)
            : base(message, innerException) {
        }
    }
}
#endif

namespace Squared.Util {
#if WINDOWS
    internal struct FindHandle : IDisposable {
        [DllImport("kernel32.dll")]
        [SuppressUnmanagedCodeSecurity()]
        static extern bool FindClose (IntPtr hFindFile);

        public IntPtr Handle;

        public FindHandle (IntPtr handle) {
            Handle = handle;
        }

        public static implicit operator IntPtr (FindHandle handle) {
            return handle.Handle;
        }

        public bool Valid {
            get {
                var value = Handle.ToInt64();
                return (value != -1) && (value != 0);
            }
        }

        public void Dispose () {
            if (Handle != IntPtr.Zero) {
                FindClose(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
#endif

    public static class IO {
#if WINDOWS
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack=1)]
        struct WIN32_FIND_DATA {
            public UInt32 dwFileAttributes;
            public Int64 ftCreationTime;
            public Int64 ftLastAccessTime;
            public Int64 ftLastWriteTime;
            public UInt32 dwFileSizeHigh;
            public UInt32 dwFileSizeLow;
            public UInt32 dwReserved0;
            public UInt32 dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO {
            public IntPtr hIcon;
            public IntPtr iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity()]
        static extern IntPtr FindFirstFile (
            string lpFileName, out WIN32_FIND_DATA lpFindFileData
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity()]
        static extern bool FindNextFile (
            IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData
        );
#endif

        const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const int FILE_ATTRIBUTE_NORMAL = 0x80;

        public struct DirectoryEntry {
            public string Name;
            public uint Attributes;
            public ulong Size;
            public long Created;
            public long LastAccessed;
            public long LastWritten;
            public bool IsDirectory;
        }

        public static Encoding DetectStreamEncoding (System.IO.Stream stream) {
            var reader = new System.IO.StreamReader(stream, true);
            var buffer = new char[256];

            reader.ReadBlock(buffer, 0, (int)Math.Min(buffer.Length, stream.Length));
            var result = reader.CurrentEncoding;

            stream.Seek(0, System.IO.SeekOrigin.Begin);
            return result;
        }

        public static Regex GlobToRegex (string glob) {
            if (glob.EndsWith(".*"))
                glob = glob.Substring(0, glob.Length - 2);
            glob = "^" + Regex.Escape(glob.ToLower()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(
                glob, 
                RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture
            );
        }

        public static IEnumerable<string> EnumDirectories (string path, string searchPattern = "*", bool recursive = false) {
            return 
                from de in 
                EnumDirectoryEntries(
                    path, searchPattern, recursive, IsDirectory
                )
                select de.Name;
        }

        public static IEnumerable<string> EnumFiles (string path, string searchPattern = "*", bool recursive = false) {
            return 
                from de in 
                EnumDirectoryEntries(
                    path, searchPattern, recursive, IsFile
                )
                select de.Name;
        }

        public static bool IsDirectory (uint attributes) {
            return (attributes & FILE_ATTRIBUTE_DIRECTORY) == FILE_ATTRIBUTE_DIRECTORY;
        }

        public static bool IsFile (uint attributes) {
            return (attributes & FILE_ATTRIBUTE_DIRECTORY) != FILE_ATTRIBUTE_DIRECTORY;
        }

        public static IEnumerable<DirectoryEntry> EnumDirectoryEntries (string path, string searchPattern, bool recursive, Func<uint, bool> attributeFilter) {
#if !WINDOWS
            throw new NotImplementedException();
#else
            if (!System.IO.Directory.Exists(path))
                throw new System.IO.DirectoryNotFoundException();

            var buffer = new StringBuilder();
            string actualPath = System.IO.Path.GetFullPath(path + @"\");
            var patterns = searchPattern.Split(';');
            var globs = (from p in patterns select GlobToRegex(p)).ToArray();
            var findData = new WIN32_FIND_DATA();
            var searchPaths = new Queue<string>();
            var entry = new DirectoryEntry();
            searchPaths.Enqueue("");

            while (searchPaths.Count != 0) {
                string currentPath = searchPaths.Dequeue();

                buffer.Remove(0, buffer.Length);
                buffer.Append(actualPath);
                buffer.Append(currentPath);
                buffer.Append("*");

                using (var handle = new FindHandle(FindFirstFile(buffer.ToString(), out findData)))
                while (handle.Valid) {
                    string fileName = findData.cFileName;

                    if ((fileName != ".") && (fileName != "..")) {
                        bool isDirectory = (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == FILE_ATTRIBUTE_DIRECTORY;
                        bool masked = !attributeFilter(findData.dwFileAttributes);

                        buffer.Remove(0, buffer.Length);
                        buffer.Append(actualPath);
                        buffer.Append(currentPath);
                        buffer.Append(fileName);

                        if (isDirectory)
                            buffer.Append("\\");

                        if (recursive && isDirectory) {
                            var subdir = buffer.ToString().Substring(actualPath.Length);
                            searchPaths.Enqueue(subdir);
                        }

                        if (!masked) {
                            string fileNameLower = fileName.ToLower();

                            bool globMatch = false;
                            foreach (var glob in globs) {
                                if (glob.IsMatch(fileNameLower)) {
                                    globMatch = true;
                                    break;
                                }
                            }

                            if (globMatch) {
                                entry.Name = buffer.ToString();
                                entry.Attributes = findData.dwFileAttributes;
                                entry.Size = findData.dwFileSizeLow + (findData.dwFileSizeHigh * ((ulong)(UInt32.MaxValue) + 1));                                
                                entry.Created = DateTime.FromFileTimeUtc(findData.ftCreationTime).Ticks;
                                entry.LastAccessed = DateTime.FromFileTimeUtc(findData.ftLastAccessTime).Ticks;
                                entry.LastWritten = DateTime.FromFileTimeUtc(findData.ftLastWriteTime).Ticks;
                                entry.IsDirectory = isDirectory;
                                yield return entry;
                            }
                        }
                    }

                    if (!FindNextFile(handle, out findData))
                        break;
                }
            }
#endif
        }
    }
}
