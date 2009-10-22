﻿#region Copyright (C) 2008-2009 Simon Allaeys

/*
    Copyright (C) 2008-2009 Simon Allaeys
 
    This file is part of AppStract

    AppStract is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    AppStract is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with AppStract.  If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Collections.Generic;
using AppStract.Core.Data.Application;
using AppStract.Core.Data.Databases;
using AppStract.Core.System.IPC;
using AppStract.Core.Virtualization.FileSystem;
using AppStract.Core.Virtualization.Process;

namespace AppStract.Core.Virtualization.Packaging
{
  /// <summary>
  /// Derived from <see cref="VirtualizedProcess"/>
  /// and extended with functionality specific to the packaging of applications.
  /// </summary>
  class PackagingProcess : VirtualizedProcess
  {

    #region Constructors

    /// <summary>
    /// Initializes a new instance of <see cref="PackagingProcess"/>.
    /// </summary>
    /// <param name="startInfo">
    /// The <see cref="VirtualProcessStartInfo"/> containing the information used to start the process with.
    /// </param>
    /// <param name="synchronizer">
    /// The <see cref="ProcessSynchronizer"/> to use for data synchronization with the <see cref="VirtualizedProcess"/>.
    /// </param>
    private PackagingProcess(VirtualProcessStartInfo startInfo, IProcessSynchronizer synchronizer)
      : base(startInfo, synchronizer)
    {

    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Starts a new <see cref="PackagingProcess"/> from the <see cref="VirtualProcessStartInfo"/> specified.
    /// </summary>
    /// <param name="startInfo">
    /// The <see cref="VirtualProcessStartInfo"/> containing the information used to start the process with.
    /// </param>
    /// <returns>
    /// A new <see cref="PackagingProcess"/> component that is associated with the process resource.
    /// </returns>
    public new static PackagingProcess Start(VirtualProcessStartInfo startInfo)
    {
      if (startInfo.Files.DatabaseRegistry.Type != FileType.Database)
        throw new ArgumentException("The destination file specified for the file system database is not valid.",
                                    "startInfo");
      if (startInfo.Files.DatabaseRegistry.Type != FileType.Database)
        throw new ArgumentException("The destination file specified for the registry database is not valid.",
                                    "startInfo");
      var registryDatabase = RegistryDatabase.CreateDefaultDatabase(startInfo.Files.DatabaseRegistry.FileName);
      registryDatabase.Initialize();
      var fileSystemDatabase = WatchingFileSystemDatabase.CreateDefaultDatabase(startInfo.Files.DatabaseFileSystem.FileName);
      fileSystemDatabase.Initialize();
      var synchronizer = new ProcessSynchronizer(fileSystemDatabase, startInfo.WorkingDirectory, registryDatabase);
      var process = new PackagingProcess(startInfo, synchronizer);
      process.Start();
      return process;
    }

    /// <summary>
    /// Returns all the executables that were detected during packaging.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<string> GetExecutables()
    {
      if (!HasExited)
        return new List<string>(0);
      var ps = _connection.ProcessSynchronizer as ProcessSynchronizer;
      if (ps == null) /// Should never occur.
        throw new InvalidCastException("An unexpected exception occured in the application workflow."
                                       + " Expected object of type "
                                       + typeof(ProcessSynchronizer)
                                       + " but received an object of type "
                                       + _connection.ProcessSynchronizer.GetType());
      var db = ps.FileSystemDatabase as WatchingFileSystemDatabase;
      if (db == null) /// Should never occur.
        throw new InvalidCastException("An unexpected exception occured in the application workflow."
                                       + " Expected object of type "
                                       + typeof(WatchingFileSystemDatabase)
                                       + " but received an object of type "
                                       + ps.FileSystemDatabase.GetType());
      var executables = new List<string>();
      foreach (FileTableEntry entry in db.Executables)
        executables.Add(entry.Key); /// BUG? Use entry.Key or entry.Value?
      return executables;
    }

    #endregion

  }
}
