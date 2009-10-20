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
using System.Data;
using System.Data.SQLite;
using System.Text;
using System.Threading;
using AppStract.Utilities.Observables;

namespace AppStract.Core.Data.Databases
{
  /// <summary>
  /// Base class for database interfaces using SQLite.
  /// </summary>
  /// <typeparam name="T">The type of objects stored and accessed by the current <see cref="Database{T}"/>.</typeparam>
  public abstract class Database<T> : IDisposable
  {

    #region Variables

    /// <summary>
    /// The connectionstring used to connect to the SQLite database.
    /// Contains only lower cased characters.
    /// </summary>
    protected readonly string _connectionString;
    /// <summary>
    /// Queue for the <see cref="DatabaseAction{T}"/>s waiting to be commited.
    /// </summary>
    private readonly ObservableQueue<DatabaseAction<T>> _actionQueue;
    /// <summary>
    /// Lock to use when working with <see cref="_actionQueue"/>.
    /// </summary>
    private readonly ReaderWriterLockSlim _actionQueueLock;
    /// <summary>
    /// Lock to use when using the database file.
    /// </summary>
    private readonly ReaderWriterLockSlim _sqliteLock;

    #endregion

    #region Events

    /// <summary>
    /// Occurs when a new item is added to the queue.
    /// </summary>
    /// <remarks>
    /// This event is synchronously called and is not thread safe.
    /// Inheritors should never unsubscribe, in order to avoid exceptions while raising the event.
    /// Inheritors should also process the event async if processing takes a long time.
    /// If an inheritor exposes this event, the inheritor should implement it's own functionality to make it thread safe.
    /// </remarks>
    protected event EventHandler<DatabaseAction<T>> ItemEnqueued;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the connection string used by the current instance.
    /// </summary>
    public string ConnectionString
    {
      get { return _connectionString; }
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of <see cref="Database{T}"/>.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    /// <param name="connectionString">The connectionstring to use to connect to the SQLite database.</param>
    protected Database(string connectionString)
    {
      _connectionString = connectionString.ToLowerInvariant();
      /// Do a basic check on the connectionstring.
      if (!_connectionString.Contains("data source="))
        throw new ArgumentException("The connectionstring must at least specify a data source.");
      _sqliteLock = new ReaderWriterLockSlim();
      _actionQueueLock = new ReaderWriterLockSlim();
      _actionQueue = new ObservableQueue<DatabaseAction<T>>();
      _actionQueue.ItemEnqueued += OnActionEnqueued;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Initializes the database.
    /// If the SQLite file doesn't exist yet, it is created.
    /// If the tables don't exist yet, they are created.
    /// </summary>
    public abstract void Initialize();

    /// <summary>
    /// Reads the complete database to an <see cref="IEnumerable{T}"/>.
    /// </summary>
    /// <returns></returns>
    public abstract IEnumerable<T> ReadAll();

    /// <summary>
    /// Enqueues an action to be commited to the database.
    /// </summary>
    /// <param name="databaseAction"></param>
    public void EnqueueAction(DatabaseAction<T> databaseAction)
    {
      /// Don't acquire a write lock, this class can handle the enqueueing of new items while flushing.
      /// A lock is only necessary when dequeueing items.
      _actionQueue.Enqueue(databaseAction);
    }

    /// <summary>
    /// Enqueues multiple actions to be commited to the database.
    /// </summary>
    /// <param name="databaseActions"></param>
    public void EnqueueAction(IEnumerable<DatabaseAction<T>> databaseActions)
    {
      /// Don't acquire a write lock, this class can handle the enqueueing of new items while flushing.
      /// A lock is only necessary when dequeueing items.
      _actionQueue.Enqueue(databaseActions, false);
    }

    #endregion

    #region Protected Methods

    /// <summary>
    /// Returns whether the table with the specified <paramref name="tableName"/> exists.
    /// If the table does not exist and <paramref name="creationQuery"/> is not null, the table is created.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// An <see cref="ArgumentNullException"/> is thrown if <paramref name="tableName"/> is null.
    /// </exception>
    /// <exception cref="DatabaseException">
    /// A <see cref="DatabaseException"/> is thrown when the method is unable to create the table.
    /// </exception>
    /// <param name="tableName">Table to verify.</param>
    /// <param name="creationQuery">
    /// Query to use if the table needs to be created.
    /// If this parameter is null, the method behaves like an Exists() method.
    /// </param>
    /// <param name="clearTableIfExists">
    /// Whether the table must be cleared if it already exists, this deletes all rows from the table.
    /// </param>
    protected bool VerifyTable(string tableName, string creationQuery, bool clearTableIfExists)
    {
      if (tableName == null)
        throw new ArgumentNullException("tableName");
      try
      {
        _sqliteLock.EnterUpgradeableReadLock();
        using (var connection = new SQLiteConnection(_connectionString))
        {
          var command = new SQLiteCommand("SELECT * FROM \"" + tableName + "\" LIMIT 1", connection);
          command.Connection.Open();
          try
          {
            command.ExecuteReader().Close();
            if (!clearTableIfExists)
              return true;
            try
            {
              command.CommandText = "DELETE FROM \"" + tableName + "\"";
              command.ExecuteNonQuery();
            }
            catch (Exception e)
            {
              CoreBus.Log.Debug("[Database] Failed to clear table \"{0}\"", e, tableName);
            }
            return true;
          }
          catch (SQLiteException)
          {
            CoreBus.Log.Debug("[Database] Failed to verify existance of table \"{0}\"", tableName);
            /// Create the table.
            if (creationQuery != null)
            {
              command = new SQLiteCommand(creationQuery, connection);
              if (!ExecuteCommand(command))
                throw new Exception("[Database] Unable to create table\"" + tableName
                                    + "\" with the following query: " + creationQuery);
            }
            return false;
          }
        }
      }
      finally
      {
        _sqliteLock.ExitUpgradeableReadLock();
      }
    }

    /// <summary>
    /// Reads all values from the specified <paramref name="columns"/> in the specified <paramref name="tables"/>.
    /// The items are build by calling <paramref name="itemBuilder"/>.
    /// </summary>
    /// <exception cref="ArgumentException">An <see cref="ArgumentException"/> is thrown when there are no tables defined.</exception>
    /// <exception cref="ArgumentNullException">An <see cref="ArgumentNullException"/> is thrown if one of the parameters is null.</exception>
    /// <param name="tables">The tables in the database to read from.</param>
    /// <param name="columns">The columns to read all values from.</param>
    /// <param name="itemBuilder">
    /// The method to use when building the item. The <see cref="IDataRecord"/>'s fields
    /// are in the same order as the columns in the <paramref name="columns"/> parameter.
    /// </param>
    /// <returns>A list of all items.</returns>
    protected IList<ItemType> ReadAll<ItemType>(IEnumerable<string> tables, IEnumerable<string> columns,
      BuildItemFromQueryData<ItemType> itemBuilder)
    {
      List<ItemType> entries = new List<ItemType>();
      try
      {
        _sqliteLock.EnterReadLock();
        using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
        {
          string query = BuildSelectQuery(tables, columns).ToString();
          SQLiteCommand command = new SQLiteCommand(query.EndsWith(";") ? query : query + ";", connection);
          command.Connection.Open();
          SQLiteDataReader reader = command.ExecuteReader();
          while (reader.Read())
            entries.Add(itemBuilder(reader));
        }
      }
      finally
      {
        _sqliteLock.ExitReadLock();
      }
      return entries;
    }

    /// <summary>
    /// Reads all values from the specified <paramref name="columns"/> in the specified <paramref name="tables"/>.
    /// The items are build by calling <paramref name="itemBuilder"/>.
    /// </summary>
    /// <exception cref="ArgumentException">An <see cref="ArgumentException"/> is thrown when there are no tables defined.</exception>
    /// <exception cref="ArgumentNullException">An <see cref="ArgumentNullException"/> is thrown if one of the parameters is null.</exception>
    /// <param name="tables">The tables in the database to read from.</param>
    /// <param name="columns">The columns to read all values from.</param>
    /// <param name="conditionals">The elements for the where keyword.</param>
    /// <param name="itemBuilder">
    /// The method to use when building the item. The <see cref="IDataRecord"/>'s fields
    /// are in the same order as the columns in the <paramref name="columns"/> parameter.
    /// </param>
    /// <returns>A list of all items.</returns>
    protected IList<ItemType> ReadAll<ItemType>(IEnumerable<string> tables, IEnumerable<string> columns,
      IEnumerable<KeyValuePair<object, object>> conditionals, BuildItemFromQueryData<ItemType> itemBuilder)
    {
      List<ItemType> entries = new List<ItemType>();
      try
      {
        _sqliteLock.EnterReadLock();
        using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
        {
          string query = BuildSelectQuery(tables, columns, conditionals).ToString();
          SQLiteCommand command = new SQLiteCommand(query.EndsWith(";") ? query : query + ";", connection);
          command.Connection.Open();
          SQLiteDataReader reader = command.ExecuteReader();
          while (reader.Read())
            entries.Add(itemBuilder(reader));
        }
      }
      finally
      {
        _sqliteLock.ExitReadLock();
      }
      return entries;
    }

    /// <summary>
    /// Escapes a minimal set of characters by replacing them with their escape codes. 
    /// This instructs the database engine to interpret these characters literally rather than as metacharacters.
    /// </summary>
    /// <seealso cref="Unescape"/>
    /// <param name="str">The input string containing the text to convert.</param>
    /// <returns></returns>
    protected static string Escape(string str)
    {
      str = str.Replace("\"", "#~&DoUBleQuOte$~#");
      str = str.Replace("'", "#~&SiNgLeQuOte$~#");
      str = str.Replace("`", "#~&SiNgLeQuOteOpEn$~#");
      str = str.Replace("´", "#~&SiNgLeQuOte3ClOSe$~#");
      str = str.Replace("(", "#~&BrACKetOpEn$~#");
      str = str.Replace(")", "#~&BrACKetClOSe$~#");
      str = str.Replace("%", "#~&ProCent$~#");
      return str;
    }

    /// <summary>
    /// Unescapes any escaped characters in the input string.
    /// </summary>
    /// <param name="str">The input string containing the text to unescape.</param>
    /// <returns></returns>
    protected static string Unescape(string str)
    {
      str = str.Replace("#~&DoUBleQuOte$~#", "\"");
      str = str.Replace("#~&SiNgLeQuOte$~#", "'");
      str = str.Replace("#~&SiNgLeQuOteOpEn$~#", "`");
      str = str.Replace("#~&SiNgLeQuOteClOSe$~#", "´");
      str = str.Replace("#~&BrACKetOpEn$~#", "(");
      str = str.Replace("#~&BrACKetClOSe$~#", ")");
      str = str.Replace("#~&ProCent$~#", "%");
      return str;

    }

    /// <summary>
    /// Appends a delete-query for <paramref name="item"/> to <paramref name="command"/>.
    /// The parameters are added to the Parameters of <paramref name="command"/>,
    /// the (unique) naming of these parameters is done by using <paramref name="seed"/>.
    /// </summary>
    /// <param name="command">The command to append the query to.</param>
    /// <param name="seed">The object to use for the generation of unique names.</param>
    /// <param name="item"></param>
    /// <returns></returns>
    protected abstract void AppendDeleteQuery(SQLiteCommand command, ParameterGenerator seed, T item);

    /// <summary>
    /// Appends an insert-query for <paramref name="item"/> to <paramref name="command"/>.
    /// The parameters are added to the Parameters of <paramref name="command"/>,
    /// the (unique) naming of these parameters is done by using <paramref name="seed"/>.
    /// </summary>
    /// <param name="command"></param>
    /// <param name="seed"></param>
    /// <param name="item"></param>
    /// <returns></returns>
    protected abstract void AppendInsertQuery(SQLiteCommand command, ParameterGenerator seed, T item);

    /// <summary>
    /// Appends an update-query for <paramref name="item"/> to <paramref name="command"/>.
    /// The parameters are added to the Parameters of <paramref name="command"/>,
    /// the (unique) naming of these parameters is done by using <paramref name="seed"/>.
    /// </summary>
    /// <param name="command"></param>
    /// <param name="seed"></param>
    /// <param name="item"></param>
    /// <returns></returns>
    protected abstract void AppendUpdateQuery(SQLiteCommand command, ParameterGenerator seed, T item);

    #endregion

    #region Private Methods

    /// <summary>
    /// Handles newly enqueued <see cref="DatabaseAction{T}"/>s.
    /// </summary>
    /// <param name="action"></param>
    private void OnActionEnqueued(DatabaseAction<T> action)
    {
      ItemEnqueued(this, action);
      try
      {
        Flush();
      }
      catch (Exception e)
      {
        CoreBus.Log.Error("Failed to flush to database", e);
      }
    }

    /// <summary>
    /// Flushes all queued <see cref="DatabaseAction{TItem}"/>s to the physical database.
    /// It's not guaranteed that it's the current thread that will perform the flushing!
    /// </summary>
    /// <exception cref="Exception">
    /// An <see cref="Exception"/> is thrown if flushing failes.
    /// </exception>
    private void Flush()
    {
      if (!_actionQueueLock.TryEnterWriteLock(200))
        /// Already flushing, return.
        return;
      SQLiteConnection connection = new SQLiteConnection(_connectionString);
      SQLiteCommand command = new SQLiteCommand(connection);
      try
      {
        try
        {
          ParameterGenerator seed = new ParameterGenerator();
          while (_actionQueue.Count > 0)
          {
            var item = _actionQueue.Dequeue();
            if (item.ActionType == DatabaseActionType.Update)
              AppendUpdateQuery(command, seed, item.Item);
            else if (item.ActionType == DatabaseActionType.Set)
              AppendInsertQuery(command, seed, item.Item);
            else if (item.ActionType == DatabaseActionType.Remove)
              AppendDeleteQuery(command, seed, item.Item);
            else
              throw new NotImplementedException(
                "DatabaseActionType is changed without adding an extra handler to the Database.FlushQueue() method.");
          }
        }
        finally
        {
          _actionQueueLock.ExitWriteLock();
        }
        /// Execute the command as a single transaction to improve speed.
        command.CommandText = "BEGIN;" + command.CommandText + "COMMIT;";
        if (!ExecuteCommand(command))
          throw new Exception("Failed to flush the database.");
      }
      finally
      {
        command.Dispose();
        connection.Dispose();
      }
    }

    /// <summary>
    /// Executes the given <paramref name="command"/> as a NonQuery.
    /// This method will enter a writelock on <see cref="_sqliteLock"/>..
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    private bool ExecuteCommand(IDbCommand command)
    {
      _sqliteLock.EnterWriteLock();
      try
      {
        if (command.Connection.State != ConnectionState.Open)
          command.Connection.Open();
        command.ExecuteNonQuery();
        return true;
      }
      catch
      {
        return false;
      }
      finally
      {
        _sqliteLock.ExitWriteLock();
      }
    }

    /// <summary>
    /// Returns a query which selects the specified <paramref name="fields"/> from the specified <paramref name="tables"/>.
    /// </summary>
    /// <param name="tables">The tables to select fields from.</param>
    /// <param name="fields">The fields to select from the tables.</param>
    /// <returns></returns>
    private static StringBuilder BuildSelectQuery(IEnumerable<string> tables, IEnumerable<string> fields)
    {
      if (tables == null)
        throw new ArgumentNullException("tables");
      if (fields == null)
        throw new ArgumentNullException("fields");
      StringBuilder queryBuilder = new StringBuilder("SELECT ");
      /// Add columns to the query.
      foreach (string field in fields)
        queryBuilder.Append(field + ", ");
      if (queryBuilder.ToString().EndsWith(", "))
        queryBuilder.Remove(queryBuilder.Length - 2, 2);
      else
        /// No columns to select? Select everything.
        queryBuilder.Append("*");
      queryBuilder.Append(" FROM ");
      /// Add tables to the query.
      foreach (string table in tables)
        queryBuilder.Append(table + ", ");
      if (queryBuilder.ToString().EndsWith(", "))
        queryBuilder.Remove(queryBuilder.Length - 2, 2);
      else
        /// No tables? Can't build a query without tables!
        throw new ArgumentException("Unable to build the query because there are no tables defined.", "tables");
      return queryBuilder;
    }

    /// <summary>
    /// Returns a query which selects the specified <paramref name="fields"/> from the specified <paramref name="tables"/>.
    /// </summary>
    /// <param name="tables">The tables to select fields from.</param>
    /// <param name="fields">The fields to select from the tables.</param>
    /// <param name="conditions">The conditions to select on using the WHERE keyword.</param>
    /// <returns></returns>
    private static StringBuilder BuildSelectQuery(IEnumerable<string> tables, IEnumerable<string> fields, IEnumerable<KeyValuePair<object, object>> conditions)
    {
      if (conditions == null)
        throw new ArgumentNullException("conditions");
      StringBuilder queryBuilder = BuildSelectQuery(tables, fields);
      if (queryBuilder[queryBuilder.Length - 1] == ';')
        queryBuilder.Remove(queryBuilder.Length - 1, 1);
      queryBuilder.Append(" WHERE ");
      /// Add the conditions.
      foreach (KeyValuePair<object, object> condition in conditions)
        queryBuilder.Append(condition.Key + " = " + condition.Value + " AND ");
      if (queryBuilder.ToString().EndsWith(" AND "))
        queryBuilder.Remove(queryBuilder.Length - 5, 5);
      else
        /// No conditions? Remove the "WHERE keyword.
        queryBuilder.Remove(queryBuilder.Length - 7, 7);
      return queryBuilder;
    }

    #endregion

    #region IDisposable Members

    public void Dispose()
    {
      /// BUG: This call could return before the current instance is really flushed.
      try
      {
        Flush();
      }
      catch (Exception e)
      {
        CoreBus.Log.Error("Failed to flush to database", e);
      }
    }

    #endregion

  }
}