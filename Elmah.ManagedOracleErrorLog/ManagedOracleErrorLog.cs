#region License, Terms and Author(s)
//
// ELMAH - Error Logging Modules and Handlers for ASP.NET
// Copyright (c) 2004-9 Atif Aziz. All rights reserved.
//
//  Author(s):
//
//      James Driscoll, mailto:jamesdriscoll@btinternet.com
//      with contributions from Hath1
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
//This module based on original OracleErrorLog from Elmah
//Modified by Kain Stropov
#endregion


using System;
using System.Configuration;
using System.Data;
using System.IO;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;


namespace Elmah
{
	#region Imports

	using IDictionary = System.Collections.IDictionary;
	using IList = System.Collections.IList;

	#endregion

	/// <summary>
	/// An <see cref="ErrorLog"/> implementation that uses Oracle as its backing store.
	/// </summary>

	public class ManagedOracleErrorLog : ErrorLog
	{
		private const int MaxAppNameLength = 60;
		private const int MaxSchemaNameLength = 30;
		private string _schemaOwner;
		private bool _schemaOwnerInitialized;

		/// <summary>
		/// Initializes a new instance of the <see cref="OracleErrorLog"/> class
		/// using a dictionary of configured settings.
		/// </summary>

		public ManagedOracleErrorLog(IDictionary config)
		{
			if (config == null)
				throw new ArgumentNullException(nameof(config));

			var connectionStringName = (string)config["connectionStringName"] ?? string.Empty;

			var connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;

			//
			// If there is no connection string to use then throw an 
			// exception to abort construction.
			//

			if (string.IsNullOrWhiteSpace(connectionString))
				throw new ApplicationException("Connection string is missing for the Oracle error log.");

			ConnectionString = connectionString;

			//
			// Set the application name as this implementation provides
			// per-application isolation over a single store.
			//

			var appName = (string)config["applicationName"] ?? string.Empty;

			if (appName.Length > MaxAppNameLength)
			{
				throw new ApplicationException(
					$"Application name is too long. Maximum length allowed is {MaxAppNameLength:N0} characters.");
			}

			ApplicationName = appName;


			SchemaOwner = (string)config["schemaOwner"];
		}

		/// <summary>
		/// Gets the name of this error log implementation.
		/// </summary>

		public override string Name => "Oracle Error Log with ManagedDataAccess";

		/// <summary>
		/// Gets the connection string used by the log to connect to the database.
		/// </summary>

		public string ConnectionString { get; }

		/// <summary>
		/// Gets the name of the schema owner where the errors are being stored.
		/// </summary>

		public string SchemaOwner
		{
			get => _schemaOwner ?? string.Empty;

			set
			{
				if (_schemaOwnerInitialized)
					throw new InvalidOperationException("The schema owner cannot be reset once initialized.");

				_schemaOwner = value ?? string.Empty;

				if (_schemaOwner.Length == 0)
					return;

				if (_schemaOwner.Length > MaxSchemaNameLength)
					throw new ApplicationException(
						$"Oracle schema owner is too long. Maximum length allowed is {MaxSchemaNameLength:N0} characters.");

				_schemaOwner = _schemaOwner + ".";
				_schemaOwnerInitialized = true;
			}
		}

		/// <summary>
		/// Logs an error to the database.
		/// </summary>
		/// <remarks>
		/// Use the stored procedure called by this implementation to set a
		/// policy on how long errors are kept in the log. The default
		/// implementation stores all errors for an indefinite time.
		/// </remarks>

		public override string Log(Error error)
		{
			if (error == null)
				throw new ArgumentNullException(nameof(error));

			var errorXml = ErrorXml.EncodeString(error);
			var id = Guid.NewGuid();

			using (var connection = new OracleConnection(ConnectionString))
			using (var command = connection.CreateCommand())
			{
				connection.Open();
				using (var transaction = connection.BeginTransaction())
				{
					// because we are storing the XML data in a NClob, we need to jump through a few hoops!!
					// so first we've got to operate within a transaction
					command.Transaction = transaction;

					// then we need to create a temporary lob on the database server
					command.CommandText = "declare xx nclob; begin dbms_lob.createtemporary(xx, false, 0); :tempblob := xx; end;";
					command.CommandType = CommandType.Text;

					var parameters = command.Parameters;
					parameters.Add("tempblob", OracleDbType.NClob).Direction = ParameterDirection.Output;
					command.ExecuteNonQuery();

					// now we can get a handle to the NClob
					var xmlLob = (OracleClob)parameters[0].Value;
					// create a temporary buffer in which to store the XML
					var tempbuff = Encoding.Unicode.GetBytes(errorXml);
					// and finally we can write to it!

					xmlLob.BeginChunkWrite();
					xmlLob.Write(tempbuff, 0, tempbuff.Length);
					xmlLob.EndChunkWrite();

					command.CommandText = "pkg_elmah$log_error.LogError";
					command.CommandType = CommandType.StoredProcedure;

					parameters.Clear();
					parameters.Add("v_ErrorId", OracleDbType.NVarchar2, 32).Value = id.ToString("N");
					parameters.Add("v_Application", OracleDbType.NVarchar2, MaxAppNameLength).Value = ApplicationName;
					parameters.Add("v_Host", OracleDbType.NVarchar2, 30).Value = error.HostName;
					parameters.Add("v_Type", OracleDbType.NVarchar2, 100).Value = error.Type;
					parameters.Add("v_Source", OracleDbType.NVarchar2, 60).Value = error.Source;
					parameters.Add("v_Message", OracleDbType.NVarchar2, 500).Value = error.Message;
					parameters.Add("v_User", OracleDbType.NVarchar2, 50).Value = error.User;
					parameters.Add("v_AllXml", OracleDbType.NClob).Value = xmlLob;
					parameters.Add("v_StatusCode", OracleDbType.Int32).Value = error.StatusCode;
					parameters.Add("v_TimeUtc", OracleDbType.Date).Value = error.Time.ToUniversalTime();

					command.ExecuteNonQuery();
					transaction.Commit();
				}
				return id.ToString();
			}
		}

		/// <summary>
		/// Returns a page of errors from the databse in descending order 
		/// of logged time.
		/// </summary>

		public override int GetErrors(int pageIndex, int pageSize, IList errorEntryList)
		{
			if (pageIndex < 0)
				throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, null);

			if (pageSize < 0)
				throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, null);

			using (var connection = new OracleConnection(ConnectionString))
			using (var command = connection.CreateCommand())
			{
				command.CommandText = "pkg_elmah$get_error.GetErrorsXml";
				command.CommandType = CommandType.StoredProcedure;

				var parameters = command.Parameters;

				parameters.Add("v_Application", OracleDbType.NVarchar2, MaxAppNameLength).Value = ApplicationName;
				parameters.Add("v_PageIndex", OracleDbType.Int32).Value = pageIndex;
				parameters.Add("v_PageSize", OracleDbType.Int32).Value = pageSize;
				parameters.Add("v_TotalCount", OracleDbType.Int32).Direction = ParameterDirection.Output;
				parameters.Add("v_Results", OracleDbType.RefCursor).Direction = ParameterDirection.Output;

				connection.Open();

				using (var reader = command.ExecuteReader())
				{

					if (errorEntryList != null)
					{
						while (reader.Read())
						{
							var id = reader["ErrorId"].ToString();
							var guid = new Guid(id);

							var error = new Error
							{
								ApplicationName = reader["Application"].ToString(),
								HostName = reader["Host"].ToString(),
								Type = reader["Type"].ToString(),
								Source = reader["Source"].ToString(),
								Message = reader["Message"].ToString(),
								User = reader["UserName"].ToString(),
								StatusCode = Convert.ToInt32(reader["StatusCode"]),
								Time = Convert.ToDateTime(reader["TimeUtc"]).ToLocalTime()
							};


							errorEntryList.Add(new ErrorLogEntry(this, guid.ToString(), error));
						}
					}
					reader.Close();
				}

				return ((OracleDecimal)command.Parameters["v_TotalCount"].Value).ToInt32();
			}
		}

		/// <summary>
		/// Returns the specified error from the database, or null 
		/// if it does not exist.
		/// </summary>

		public override ErrorLogEntry GetError(string id)
		{
			if (id == null)
				throw new ArgumentNullException(nameof(id));

			if (id.Length == 0)
				throw new ArgumentException(null, nameof(id));

			Guid errorGuid;

			try
			{
				errorGuid = new Guid(id);
			}
			catch (FormatException e)
			{
				throw new ArgumentException(e.Message, nameof(id), e);
			}

			string errorXml;

			using (var connection = new OracleConnection(ConnectionString))
			using (var command = connection.CreateCommand())
			{
				command.CommandText = "pkg_elmah$get_error.GetErrorXml";
				command.CommandType = CommandType.StoredProcedure;

				var parameters = command.Parameters;
				parameters.Add("v_Application", OracleDbType.NVarchar2, MaxAppNameLength).Value = ApplicationName;
				parameters.Add("v_ErrorId", OracleDbType.NVarchar2, 32).Value = errorGuid.ToString("N");
				parameters.Add("v_AllXml", OracleDbType.NClob).Direction = ParameterDirection.Output;

				connection.Open();
				command.ExecuteNonQuery();
				var xmlLob = (OracleClob)command.Parameters["v_AllXml"].Value;

				var streamreader = new StreamReader(xmlLob, Encoding.Unicode);
				var cbuffer = new char[1000];
				int actual;
				var sb = new StringBuilder();
				while ((actual = streamreader.Read(cbuffer, 0, cbuffer.Length)) > 0)
					sb.Append(cbuffer, 0, actual);
				errorXml = sb.ToString();
			}

			if (string.IsNullOrEmpty(errorXml))
				return null;

			var error = ErrorXml.DecodeString(errorXml);
			return new ErrorLogEntry(this, id, error);
		}
	}
}
