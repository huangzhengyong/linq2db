﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;

using JetBrains.Annotations;

namespace LinqToDB.Data
{
	using Common;
	using Configuration;
	using DataProvider;
	using Expressions;
	using Mapping;

	public partial class DataConnection : ICloneable
	{
		#region .ctor

		public DataConnection() : this(null)
		{
		}

		public DataConnection(string configurationString)
		{
			InitConfig();

			ConfigurationString = configurationString ?? DefaultConfiguration;

			if (ConfigurationString == null)
				throw new LinqToDBException("Configuration string is not provided.");

			var ci = GetConfigurationInfo(ConfigurationString);

			DataProvider     = ci.DataProvider;
			ConnectionString = ci.ConnectionString;
			_mappingSchema   = DataProvider.MappingSchema;
		}

		public DataConnection([JetBrains.Annotations.NotNull] string providerName, [JetBrains.Annotations.NotNull] string connectionString)
		{
			if (providerName     == null) throw new ArgumentNullException(nameof(providerName));
			if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

			IDataProvider dataProvider;

			if (!_dataProviders.TryGetValue(providerName, out dataProvider))
				throw new LinqToDBException($"DataProvider '{providerName}' not found.");

			InitConfig();

			DataProvider     = dataProvider;
			ConnectionString = connectionString;
			_mappingSchema   = DataProvider.MappingSchema;
		}

		public DataConnection([JetBrains.Annotations.NotNull] IDataProvider dataProvider, [JetBrains.Annotations.NotNull] string connectionString)
		{
			if (dataProvider     == null) throw new ArgumentNullException(nameof(dataProvider));
			if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

			InitConfig();

			DataProvider     = dataProvider;
			_mappingSchema   = DataProvider.MappingSchema;
			ConnectionString = connectionString;
		}

		public DataConnection([JetBrains.Annotations.NotNull] IDataProvider dataProvider, [JetBrains.Annotations.NotNull] IDbConnection connection)
		{
			if (dataProvider == null) throw new ArgumentNullException(nameof(dataProvider));
			if (connection   == null) throw new ArgumentNullException(nameof(connection));
			
			InitConfig();

			if (!Configuration.AvoidSpecificDataProviderAPI && !dataProvider.IsCompatibleConnection(connection))
				throw new LinqToDBException($"DataProvider '{dataProvider}' and connection '{connection}' are not compatible.");

			DataProvider   = dataProvider;
			_mappingSchema = DataProvider.MappingSchema;
			_connection    = connection;
		}

		public DataConnection([JetBrains.Annotations.NotNull] IDataProvider dataProvider, [JetBrains.Annotations.NotNull] IDbTransaction transaction)
		{
			if (dataProvider == null) throw new ArgumentNullException(nameof(dataProvider));
			if (transaction  == null) throw new ArgumentNullException(nameof(transaction));
			
			InitConfig();

			if (!Configuration.AvoidSpecificDataProviderAPI && !dataProvider.IsCompatibleConnection(transaction.Connection))
				throw new LinqToDBException($"DataProvider '{dataProvider}' and connection '{transaction.Connection}' are not compatible.");

			DataProvider      = dataProvider;
			_mappingSchema    = DataProvider.MappingSchema;
			_connection       = transaction.Connection;
			Transaction       = transaction;
			_closeTransaction = false;
		}

		#endregion

		#region Public Properties

		public string        ConfigurationString { get; }
		public IDataProvider DataProvider        { get; }
		public string        ConnectionString    { get; }

		static readonly ConcurrentDictionary<string,int> _configurationIDs;
		static int _maxID;

		private int? _id;
		public  int   ID
		{
			get
			{
				if (!_id.HasValue)
				{
					var key = MappingSchema.ConfigurationID + "." + (ConfigurationString ?? ConnectionString ?? Connection.ConnectionString);
					int id;

					if (!_configurationIDs.TryGetValue(key, out id))
						_configurationIDs[key] = id = Interlocked.Increment(ref _maxID);

					_id = id;
				}

				return _id.Value;
			}
		}

		private bool? _isMarsEnabled;
		public  bool   IsMarsEnabled
		{
			get
			{
				if (_isMarsEnabled == null)
					_isMarsEnabled = (bool)(DataProvider.GetConnectionInfo(this, "IsMarsEnabled") ?? false);

				return _isMarsEnabled.Value;
			}
			set { _isMarsEnabled = value; }
		}

		public static string DefaultConfiguration { get; set; }
		public static string DefaultDataProvider  { get; set; }

		private static Action<TraceInfo> _onTrace = OnTraceInternal;
		public  static Action<TraceInfo>  OnTrace
		{
			get { return _onTrace; }
			set { _onTrace = value ?? OnTraceInternal; }
		}

		[CanBeNull]
		public  Action<TraceInfo>  OnTraceConnection { get; set; } = OnTrace;

		static void OnTraceInternal(TraceInfo info)
		{
			switch (info.TraceInfoStep)
			{
				case TraceInfoStep.BeforeExecute:
					WriteTraceLine(info.SqlText, TraceSwitch.DisplayName);
					break;

				case TraceInfoStep.AfterExecute:
					WriteTraceLine(
						info.RecordsAffected != null
							? $"Query Execution Time: {info.ExecutionTime}. Records Affected: {info.RecordsAffected}.\r\n"
							: $"Query Execution Time: {info.ExecutionTime}\r\n",
						TraceSwitch.DisplayName);
					break;

				case TraceInfoStep.Error:
					{
						var sb = new StringBuilder();

						for (var ex = info.Exception; ex != null; ex = ex.InnerException)
						{
							sb
								.AppendLine()
								.AppendLine($"Exception: {ex.GetType()}")
								.AppendLine($"Message  : {ex.Message}")
								.AppendLine(ex.StackTrace)
								;
						}

						WriteTraceLine(sb.ToString(), TraceSwitch.DisplayName);
					
					}

					break;

				case TraceInfoStep.MapperCreated:
					{
						var sb = new StringBuilder();

						if (Configuration.Linq.TraceMapperExpression && info.MapperExpression != null)
							sb.AppendLine(info.MapperExpression.GetDebugView());

						WriteTraceLine(sb.ToString(), TraceSwitch.DisplayName);
					}

					break;

				case TraceInfoStep.Completed:
					{
						var sb = new StringBuilder();

						sb.Append($"Total Execution Time: {info.ExecutionTime}.");

						if (info.RecordsAffected != null)
							sb.Append($" Rows Count: {info.RecordsAffected}.");

						sb.AppendLine();

						WriteTraceLine(sb.ToString(), TraceSwitch.DisplayName);
					}

					break;
			}
		}

		private static TraceSwitch _traceSwitch;
		public  static TraceSwitch  TraceSwitch
		{
			get
			{
				return _traceSwitch ?? (_traceSwitch = new TraceSwitch("DataConnection", "DataConnection trace switch",
#if DEBUG
				"Warning"
#else
				"Off"
#endif
				));
			}
			set { _traceSwitch = value; }
		}

		public static void TurnTraceSwitchOn(TraceLevel traceLevel = TraceLevel.Info)
		{
			TraceSwitch = new TraceSwitch("DataConnection", "DataConnection trace switch", traceLevel.ToString());
		}

		public static Action<string,string> WriteTraceLine = (message, displayName) => Debug.WriteLine(message, displayName);

		#endregion

		#region Configuration

		static IDataProvider FindProvider(string configuration, ICollection<KeyValuePair<string,IDataProvider>> ps, IDataProvider defp)
		{
			foreach (var p in ps.OrderByDescending(kv => kv.Key.Length))
				if (configuration == p.Key || configuration.StartsWith(p.Key + '.'))
					return p.Value;

			foreach (var p in ps.OrderByDescending(kv => kv.Value.Name.Length))
				if (configuration == p.Value.Name || configuration.StartsWith(p.Value.Name + '.'))
					return p.Value;

			return defp;
		}

		static DataConnection()
		{
			_configurationIDs = new ConcurrentDictionary<string,int>();

			LinqToDB.DataProvider.SqlServer. SqlServerTools. GetDataProvider();
			LinqToDB.DataProvider.Access.    AccessTools.    GetDataProvider();
			LinqToDB.DataProvider.SqlCe.     SqlCeTools.     GetDataProvider();
			LinqToDB.DataProvider.Firebird.  FirebirdTools.  GetDataProvider();
			LinqToDB.DataProvider.MySql.     MySqlTools.     GetDataProvider();
			LinqToDB.DataProvider.SQLite.    SQLiteTools.    GetDataProvider();
			LinqToDB.DataProvider.Sybase.    SybaseTools.    GetDataProvider();
			LinqToDB.DataProvider.Oracle.    OracleTools.    GetDataProvider();
			LinqToDB.DataProvider.PostgreSQL.PostgreSQLTools.GetDataProvider();
			LinqToDB.DataProvider.DB2.       DB2Tools.       GetDataProvider();
			LinqToDB.DataProvider.Informix.  InformixTools.  GetDataProvider();
			LinqToDB.DataProvider.SapHana.   SapHanaTools.   GetDataProvider(); 

			var section = LinqToDBSection.Instance;

			if (section != null)
			{
				DefaultConfiguration = section.DefaultConfiguration;
				DefaultDataProvider  = section.DefaultDataProvider;

				foreach (DataProviderElement provider in section.DataProviders)
				{
					var dataProviderType = Type.GetType(provider.TypeName, true);
					var providerInstance = (IDataProviderFactory)Activator.CreateInstance(dataProviderType);

					if (!string.IsNullOrEmpty(provider.Name))
						AddDataProvider(provider.Name, providerInstance.GetDataProvider(provider.Attributes));
				}
			}
		}

		static readonly List<Func<ConnectionStringSettings,IDataProvider>> _providerDetectors =
			new List<Func<ConnectionStringSettings,IDataProvider>>();

		public static void AddProviderDetector(Func<ConnectionStringSettings,IDataProvider> providerDetector)
		{
			_providerDetectors.Add(providerDetector);
		}

		internal static bool IsMachineConfig(ConnectionStringSettings css)
		{
			string source;

			try
			{
				source = css.ElementInformation.Source;
			}
			catch (Exception)
			{
				source = "";
			}

			return source == null || source.EndsWith("machine.config", StringComparison.OrdinalIgnoreCase);
		}

		static void InitConnectionStrings()
		{
			foreach (ConnectionStringSettings css in ConfigurationManager.ConnectionStrings)
			{
				_configurations[css.Name] = new ConfigurationInfo(css);

				if (DefaultConfiguration == null && !IsMachineConfig(css))
				{
					DefaultConfiguration = css.Name;
				}
			}
		}

		static int _isInitialized;

		static void InitConfig()
		{
			if (Interlocked.Exchange(ref _isInitialized, 1) == 0)
				InitConnectionStrings();
		}

		static readonly ConcurrentDictionary<string,IDataProvider> _dataProviders =
			new ConcurrentDictionary<string,IDataProvider>();

		public static void AddDataProvider([JetBrains.Annotations.NotNull] string providerName, [JetBrains.Annotations.NotNull] IDataProvider dataProvider)
		{
			if (providerName == null) throw new ArgumentNullException(nameof(providerName));
			if (dataProvider == null) throw new ArgumentNullException(nameof(dataProvider));

			if (string.IsNullOrEmpty(dataProvider.Name))
				throw new ArgumentException("dataProvider.Name cannot be empty.", nameof(dataProvider));

			_dataProviders[providerName] = dataProvider;
		}

		public static void AddDataProvider([JetBrains.Annotations.NotNull] IDataProvider dataProvider)
		{
			if (dataProvider == null) throw new ArgumentNullException(nameof(dataProvider));

			AddDataProvider(dataProvider.Name, dataProvider);
		}

		public static IDataProvider GetDataProvider([JetBrains.Annotations.NotNull] string configurationString)
		{
			InitConfig();

			return GetConfigurationInfo(configurationString).DataProvider;
		}

		class ConfigurationInfo
		{
			public ConfigurationInfo(string connectionString, IDataProvider dataProvider)
			{
				ConnectionString = connectionString;
				DataProvider     = dataProvider;
			}

			public ConfigurationInfo(ConnectionStringSettings connectionStringSettings)
			{
				ConnectionString = connectionStringSettings.ConnectionString;

				_connectionStringSettings = connectionStringSettings;
			}

			public  string ConnectionString;

			private readonly ConnectionStringSettings _connectionStringSettings;

			private IDataProvider _dataProvider;
			public  IDataProvider  DataProvider
			{
				get { return _dataProvider ?? (_dataProvider = GetDataProvider(_connectionStringSettings)); }
				private set { _dataProvider = value; }
			}

			static IDataProvider GetDataProvider(ConnectionStringSettings css)
			{
				var configuration = css.Name;
				var providerName  = css.ProviderName;
				var dataProvider  = _providerDetectors.Select(d => d(css)).FirstOrDefault(dp => dp != null);

				if (dataProvider == null)
				{
					var defaultDataProvider = DefaultDataProvider != null ? _dataProviders[DefaultDataProvider] : null;

					if (string.IsNullOrEmpty(providerName))
						dataProvider = FindProvider(configuration, _dataProviders, defaultDataProvider);
					else if (_dataProviders.ContainsKey(providerName))
						dataProvider = _dataProviders[providerName];
					else if (_dataProviders.ContainsKey(configuration))
						dataProvider = _dataProviders[configuration];
					else
					{
						var providers = _dataProviders.Where(dp => dp.Value.ConnectionNamespace == providerName).ToList();

						switch (providers.Count)
						{
							case 0  : dataProvider = defaultDataProvider;                                        break;
							case 1  : dataProvider = providers[0].Value;                                         break;
							default : dataProvider = FindProvider(configuration, providers, providers[0].Value); break;
						}
					}
				}

				if (dataProvider != null && DefaultConfiguration == null && !IsMachineConfig(css))
				{
					DefaultConfiguration = css.Name;
				}

				return dataProvider;
			}
		}

		static ConfigurationInfo GetConfigurationInfo(string configurationString)
		{
			ConfigurationInfo ci;

			if (_configurations.TryGetValue(configurationString ?? DefaultConfiguration, out ci))
				return ci;

			throw new LinqToDBException($"Configuration '{configurationString}' is not defined.");
		}

		public static void SetConnectionStrings(System.Configuration.Configuration config)
		{
			foreach (ConnectionStringSettings css in config.ConnectionStrings.ConnectionStrings)
			{
				_configurations[css.Name] = new ConfigurationInfo(css);

				if (DefaultConfiguration == null && !IsMachineConfig(css))
				{
					DefaultConfiguration = css.Name;
				}
			}
		}

		static readonly ConcurrentDictionary<string,ConfigurationInfo> _configurations =
			new ConcurrentDictionary<string, ConfigurationInfo>();

		public static void AddConfiguration(
			[JetBrains.Annotations.NotNull] string configuration,
			[JetBrains.Annotations.NotNull] string connectionString,
			IDataProvider dataProvider = null)
		{
			if (configuration    == null) throw new ArgumentNullException(nameof(configuration));
			if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

			_configurations[configuration] = new ConfigurationInfo(
				connectionString,
				dataProvider ?? FindProvider(configuration, _dataProviders, _dataProviders[DefaultDataProvider]));
		}

		public static void SetConnectionString(
			[JetBrains.Annotations.NotNull] string configuration,
			[JetBrains.Annotations.NotNull] string connectionString)
		{
			if (configuration    == null) throw new ArgumentNullException(nameof(configuration));
			if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

			InitConfig();

			_configurations[configuration].ConnectionString = connectionString;
		}

		[Pure]
		public static string GetConnectionString(string configurationString)
		{
			InitConfig();

			ConfigurationInfo ci;

			if (_configurations.TryGetValue(configurationString, out ci))
				return ci.ConnectionString;

			throw new LinqToDBException($"Configuration '{configurationString}' is not defined.");
		}

		#endregion

		#region Connection

		bool          _closeConnection;
		bool          _closeTransaction;
		IDbConnection _connection;

		public IDbConnection Connection
		{
			get
			{
				if (_connection == null)
					_connection = DataProvider.CreateConnection(ConnectionString);

				if (_connection.State == ConnectionState.Closed)
				{
					_connection.Open();
					_closeConnection = true;
				}

				return _connection;
			}
		}

		public event EventHandler OnClosing;
		public event EventHandler OnClosed;

		public virtual void Close()
		{
			if (OnClosing != null)
				OnClosing(this, EventArgs.Empty);

			DisposeCommand();

			if (Transaction != null && _closeTransaction)
			{
				Transaction.Dispose();
				Transaction = null;
			}

			if (_connection != null && _closeConnection)
			{
				_connection.Dispose();
				_connection = null;
			}

			if (OnClosed != null)
				OnClosed(this, EventArgs.Empty);
		}

		#endregion

		#region Command

		public string LastQuery;

		internal void InitCommand(CommandType commandType, string sql, DataParameter[] parameters, List<string> queryHints)
		{
			if (queryHints != null && queryHints.Count > 0)
			{
				var sqlProvider = DataProvider.CreateSqlBuilder();
				sql = sqlProvider.ApplyQueryHints(sql, queryHints);
				queryHints.Clear();
			}

			DataProvider.InitCommand(this, commandType, sql, parameters);
			LastQuery = Command.CommandText;
		}

		private int? _commandTimeout;
		public  int   CommandTimeout
		{
			get { return _commandTimeout ?? 0; }
			set { _commandTimeout = value;     }
		}

		private IDbCommand _command;
		public  IDbCommand  Command
		{
			get { return _command ?? (_command = CreateCommand()); }
			set { _command = value; }
		}

		public IDbCommand CreateCommand()
		{
			var command = Connection.CreateCommand();

			if (_commandTimeout.HasValue)
				command.CommandTimeout = _commandTimeout.Value;

			if (Transaction != null)
				command.Transaction = Transaction;

			return command;
		}

		public void DisposeCommand()
		{
			if (_command != null)
			{
				DataProvider.DisposeCommand(this);
				_command = null;
			}
		}

		internal int ExecuteNonQuery()
		{
			if (TraceSwitch.Level == TraceLevel.Off)
				return Command.ExecuteNonQuery();

			if (OnTraceConnection == null)
				return Command.ExecuteNonQuery();

			if (TraceSwitch.TraceInfo)
			{
				OnTraceConnection(new TraceInfo(TraceInfoStep.BeforeExecute)
				{
					TraceLevel     = TraceLevel.Info,
					DataConnection = this,
					Command        = Command,
				});
			}

			var now = DateTime.Now;

			try
			{
				var ret = Command.ExecuteNonQuery();

				if (TraceSwitch.TraceInfo)
				{
					OnTraceConnection(new TraceInfo(TraceInfoStep.AfterExecute)
					{
						TraceLevel      = TraceLevel.Info,
						DataConnection  = this,
						Command         = Command,
						ExecutionTime   = DateTime.Now - now,
						RecordsAffected = ret,
					});
				}

				return ret;
			}
			catch (Exception ex)
			{
				if (TraceSwitch.TraceError)
				{
					OnTraceConnection(new TraceInfo(TraceInfoStep.Error)
					{
						TraceLevel     = TraceLevel.Error,
						DataConnection = this,
						Command        = Command,
						ExecutionTime  = DateTime.Now - now,
						Exception      = ex,
					});
				}

				throw;
			}
		}

		object ExecuteScalar()
		{
			if (TraceSwitch.Level == TraceLevel.Off)
				return Command.ExecuteScalar();

			if (OnTraceConnection == null)
				return Command.ExecuteScalar();

			if (TraceSwitch.TraceInfo)
			{
				OnTraceConnection(new TraceInfo(TraceInfoStep.BeforeExecute)
				{
					TraceLevel     = TraceLevel.Info,
					DataConnection = this,
					Command        = Command,
				});
			}

			var now = DateTime.Now;

			try
			{
				var ret = Command.ExecuteScalar();

				if (TraceSwitch.TraceInfo)
				{
					OnTraceConnection(new TraceInfo(TraceInfoStep.AfterExecute)
					{
						TraceLevel     = TraceLevel.Info,
						DataConnection = this,
						Command        = Command,
						ExecutionTime  = DateTime.Now - now,
					});
				}

				return ret;
			}
			catch (Exception ex)
			{
				if (TraceSwitch.TraceError)
				{
					OnTraceConnection(new TraceInfo(TraceInfoStep.Error)
					{
						TraceLevel     = TraceLevel.Error,
						DataConnection = this,
						Command        = Command,
						ExecutionTime  = DateTime.Now - now,
						Exception      = ex,
					});
				}

				throw;
			}
		}

		internal IDataReader ExecuteReader()
		{
			return ExecuteReader(CommandBehavior.Default);
		}

		internal IDataReader ExecuteReader(CommandBehavior commandBehavior)
		{
			if (TraceSwitch.Level == TraceLevel.Off)
				return Command.ExecuteReader(commandBehavior);

			if (OnTraceConnection == null)
				return Command.ExecuteReader(commandBehavior);

			if (TraceSwitch.TraceInfo)
			{
				OnTraceConnection(new TraceInfo(TraceInfoStep.BeforeExecute)
				{
					TraceLevel     = TraceLevel.Info,
					DataConnection = this,
					Command        = Command,
				});
			}

			var now = DateTime.Now;

			try
			{
				var ret = Command.ExecuteReader(commandBehavior);

				if (TraceSwitch.TraceInfo)
				{
					OnTraceConnection(new TraceInfo(TraceInfoStep.AfterExecute)
					{
						TraceLevel     = TraceLevel.Info,
						DataConnection = this,
						Command        = Command,
						ExecutionTime  = DateTime.Now - now,
					});
				}

				return ret;
			}
			catch (Exception ex)
			{
				if (TraceSwitch.TraceError)
				{
					OnTraceConnection(new TraceInfo(TraceInfoStep.Error)
					{
						TraceLevel     = TraceLevel.Error,
						DataConnection = this,
						Command        = Command,
						ExecutionTime  = DateTime.Now - now,
						Exception      = ex,
					});
				}

				throw;
			}
		}

		public static void ClearObjectReaderCache()
		{
			CommandInfo.ClearObjectReaderCache();
		}

		#endregion

		#region Transaction

		public IDbTransaction Transaction { get; private set; }
		
		public virtual DataConnectionTransaction BeginTransaction()
		{
			// If transaction is open, we dispose it, it will rollback all changes.
			//
			Transaction?.Dispose();

			// Create new transaction object.
			//
			Transaction = Connection.BeginTransaction();

			_closeTransaction = true;

			// If the active command exists.
			//
			if (_command != null)
				_command.Transaction = Transaction;

			return new DataConnectionTransaction(this);
		}

		public virtual DataConnectionTransaction BeginTransaction(IsolationLevel isolationLevel)
		{
			// If transaction is open, we dispose it, it will rollback all changes.
			//
			Transaction?.Dispose();

			// Create new transaction object.
			//
			Transaction = Connection.BeginTransaction(isolationLevel);

			_closeTransaction = true;

			// If the active command exists.
			//
			if (_command != null)
				_command.Transaction = Transaction;

			return new DataConnectionTransaction(this);
		}

		public virtual void CommitTransaction()
		{
			if (Transaction != null)
			{
				Transaction.Commit();

				if (_closeTransaction)
				{
					Transaction.Dispose();
					Transaction = null;
				}
			}
		}

		public virtual void RollbackTransaction()
		{
			if (Transaction != null)
			{
				Transaction.Rollback();

				if (_closeTransaction)
				{
					Transaction.Dispose();
					Transaction = null;
				}
			}
		}

		#endregion

		#region MappingSchema

		private MappingSchema _mappingSchema;

		public  MappingSchema  MappingSchema => _mappingSchema;

		public bool InlineParameters { get; set; }

		private List<string> _queryHints;
		public  List<string>  QueryHints => _queryHints ?? (_queryHints = new List<string>());

		private List<string> _nextQueryHints;
		public  List<string>  NextQueryHints => _nextQueryHints ?? (_nextQueryHints = new List<string>());

		public DataConnection AddMappingSchema(MappingSchema mappingSchema)
		{
			_mappingSchema = new MappingSchema(mappingSchema, _mappingSchema);
			_id            = null;

			return this;
		}

		#endregion

		#region ICloneable Members

		DataConnection(string configurationString, IDataProvider dataProvider, string connectionString, IDbConnection connection, MappingSchema mappingSchema)
		{
			ConfigurationString = configurationString;
			DataProvider        = dataProvider;
			ConnectionString    = connectionString;
			_connection         = connection;
			_mappingSchema      = mappingSchema;
			_closeConnection    = true;
		}

		public object Clone()
		{
			var connection =
				_connection == null       ? null :
				_connection is ICloneable ? (IDbConnection)((ICloneable)_connection).Clone() :
				                            DataProvider.CreateConnection(ConnectionString);

			return new DataConnection(ConfigurationString, DataProvider, ConnectionString, connection, MappingSchema);
		}
		
		#endregion

		#region System.IDisposable Members

		public void Dispose()
		{
			Close();
		}

		#endregion
	}
}
