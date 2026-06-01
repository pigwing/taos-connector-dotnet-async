using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using TDengine.Driver.Client.Websocket;

namespace TDengine.Driver
{
    public class ConnectionStringBuilder : DbConnectionStringBuilder
    {
        private const string HostKey = "host";
        private const string PortKey = "port";
        private const string DatabaseKey = "db";
        private const string UsernameKey = "username";
        private const string PasswordKey = "password";
        private const string ProtocolKey = "protocol";
        private const string TimezoneKey = "timezone";
        private const string ConnTimeoutKey = "connTimeout";
        private const string ReadTimeoutKey = "readTimeout";
        private const string WriteTimeoutKey = "writeTimeout";
        private const string TokenKey = "token";
        private const string UseSSLKey = "useSSL";
        private const string EnableCompressionKey = "enableCompression";
        private const string AutoReconnectKey = "autoReconnect";
        private const string ReconnectRetryCountKey = "reconnectRetryCount";
        private const string ReconnectIntervalMsKey = "reconnectIntervalMs";
        private const string ConnectionTimezoneKey = "connectionTimezone";
        private const string BearerTokenKey = "bearerToken";
        private const string PoolingKey = "pooling";
        private const string MinPoolSizeKey = "minPoolSize";
        private const string MaxPoolSizeKey = "maxPoolSize";
        private const string PoolConnectionTimeoutKey = "poolConnectionTimeout";
        private const string PoolKeepaliveTimeKey = "poolKeepaliveTime";
        private const string PoolMaxLifetimeKey = "poolMaxLifetime";
        private const string PoolHousekeepingIntervalKey = "poolHousekeepingInterval";
        private const string PoolCreationRetryBackoffKey = "poolCreationRetryBackoff";
        private const string PoolMaxCreationRetryBackoffKey = "poolMaxCreationRetryBackoff";
        private const string PoolLeakDetectionThresholdKey = "poolLeakDetectionThreshold";


        private enum KeysEnum
        {
            Host,
            Port,
            Database,
            Username,
            Password,
            Protocol,
            Timezone,
            ConnTimeout,
            ReadTimeout,
            WriteTimeout,
            Token,
            UseSSL,
            EnableCompression,
            AutoReconnect,
            ReconnectRetryCount,
            ReconnectIntervalMs,
            ConnectionTimezone,
            BearerToken,
            Pooling,
            MinPoolSize,
            MaxPoolSize,
            PoolConnectionTimeout,
            PoolKeepaliveTime,
            PoolMaxLifetime,
            PoolHousekeepingInterval,
            PoolCreationRetryBackoff,
            PoolMaxCreationRetryBackoff,
            PoolLeakDetectionThreshold,
            Total
        }

        private string _host = string.Empty;
        private int _port = 0;
        private string _db = string.Empty;
        private string _user = string.Empty;
        private string _password = string.Empty;
        private string _protocol = TDengineConstant.ProtocolNative;
        private TimeZoneInfo _timezone = TimeZoneInfo.Local;
        private TimeSpan _connTimeout = TimeSpan.Zero;
        private TimeSpan _readTimeout = TimeSpan.Zero;
        private TimeSpan _writeTimeout = TimeSpan.Zero;
        private string _token = string.Empty;
        private bool _useSSL = false;
        private bool _enableCompression = false;
        private bool _autoReconnect = false;
        private int _reconnectRetryCount = 3;
        private int _reconnectIntervalMs = 2000;
        private TimeZoneInfo _connectionTimezone = null;
        private string _bearerToken = string.Empty;
        private bool _pooling = false;
        private int _minPoolSize = 0;
        private int _maxPoolSize = 10;
        private TimeSpan _poolConnectionTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _poolKeepaliveTime = TimeSpan.FromMinutes(2);
        private TimeSpan _poolMaxLifetime = TimeSpan.FromMinutes(30);
        private TimeSpan _poolHousekeepingInterval = TimeSpan.FromSeconds(30);
        private TimeSpan _poolCreationRetryBackoff = TimeSpan.FromMilliseconds(100);
        private TimeSpan _poolMaxCreationRetryBackoff = TimeSpan.FromSeconds(2);
        private TimeSpan _poolLeakDetectionThreshold = TimeSpan.Zero;

        private static readonly IReadOnlyList<string> KeysList;
        private static readonly IReadOnlyDictionary<string, KeysEnum> KeysDict;

        static ConnectionStringBuilder()
        {
            var list = new string[(int)KeysEnum.Total];
            list[(int)KeysEnum.Host] = HostKey;
            list[(int)KeysEnum.Port] = PortKey;
            list[(int)KeysEnum.Database] = DatabaseKey;
            list[(int)KeysEnum.Username] = UsernameKey;
            list[(int)KeysEnum.Password] = PasswordKey;
            list[(int)KeysEnum.Protocol] = ProtocolKey;
            list[(int)KeysEnum.Timezone] = TimezoneKey;
            list[(int)KeysEnum.ConnTimeout] = ConnTimeoutKey;
            list[(int)KeysEnum.ReadTimeout] = ReadTimeoutKey;
            list[(int)KeysEnum.WriteTimeout] = WriteTimeoutKey;
            list[(int)KeysEnum.Token] = TokenKey;
            list[(int)KeysEnum.UseSSL] = UseSSLKey;
            list[(int)KeysEnum.EnableCompression] = EnableCompressionKey;
            list[(int)KeysEnum.AutoReconnect] = AutoReconnectKey;
            list[(int)KeysEnum.ReconnectRetryCount] = ReconnectRetryCountKey;
            list[(int)KeysEnum.ReconnectIntervalMs] = ReconnectIntervalMsKey;
            list[(int)KeysEnum.ConnectionTimezone] = ConnectionTimezoneKey;
            list[(int)KeysEnum.BearerToken] = BearerTokenKey;
            list[(int)KeysEnum.Pooling] = PoolingKey;
            list[(int)KeysEnum.MinPoolSize] = MinPoolSizeKey;
            list[(int)KeysEnum.MaxPoolSize] = MaxPoolSizeKey;
            list[(int)KeysEnum.PoolConnectionTimeout] = PoolConnectionTimeoutKey;
            list[(int)KeysEnum.PoolKeepaliveTime] = PoolKeepaliveTimeKey;
            list[(int)KeysEnum.PoolMaxLifetime] = PoolMaxLifetimeKey;
            list[(int)KeysEnum.PoolHousekeepingInterval] = PoolHousekeepingIntervalKey;
            list[(int)KeysEnum.PoolCreationRetryBackoff] = PoolCreationRetryBackoffKey;
            list[(int)KeysEnum.PoolMaxCreationRetryBackoff] = PoolMaxCreationRetryBackoffKey;
            list[(int)KeysEnum.PoolLeakDetectionThreshold] = PoolLeakDetectionThresholdKey;
            KeysList = list;

            KeysDict = new Dictionary<string, KeysEnum>((int)KeysEnum.Total + 10, StringComparer.OrdinalIgnoreCase)
            {
                [HostKey] = KeysEnum.Host,
                [PortKey] = KeysEnum.Port,
                [DatabaseKey] = KeysEnum.Database,
                [UsernameKey] = KeysEnum.Username,
                [PasswordKey] = KeysEnum.Password,
                [ProtocolKey] = KeysEnum.Protocol,
                [TimezoneKey] = KeysEnum.Timezone,
                [ConnTimeoutKey] = KeysEnum.ConnTimeout,
                [ReadTimeoutKey] = KeysEnum.ReadTimeout,
                [WriteTimeoutKey] = KeysEnum.WriteTimeout,
                [TokenKey] = KeysEnum.Token,
                [UseSSLKey] = KeysEnum.UseSSL,
                [EnableCompressionKey] = KeysEnum.EnableCompression,
                [AutoReconnectKey] = KeysEnum.AutoReconnect,
                [ReconnectRetryCountKey] = KeysEnum.ReconnectRetryCount,
                [ReconnectIntervalMsKey] = KeysEnum.ReconnectIntervalMs,
                [ConnectionTimezoneKey] = KeysEnum.ConnectionTimezone,
                [BearerTokenKey] = KeysEnum.BearerToken,
                [PoolingKey] = KeysEnum.Pooling,
                [MinPoolSizeKey] = KeysEnum.MinPoolSize,
                ["min pool size"] = KeysEnum.MinPoolSize,
                ["minimumPoolSize"] = KeysEnum.MinPoolSize,
                ["minimum pool size"] = KeysEnum.MinPoolSize,
                ["minIdle"] = KeysEnum.MinPoolSize,
                ["poolMinIdle"] = KeysEnum.MinPoolSize,
                [MaxPoolSizeKey] = KeysEnum.MaxPoolSize,
                ["max pool size"] = KeysEnum.MaxPoolSize,
                ["maximumPoolSize"] = KeysEnum.MaxPoolSize,
                ["maximum pool size"] = KeysEnum.MaxPoolSize,
                [PoolConnectionTimeoutKey] = KeysEnum.PoolConnectionTimeout,
                ["connectionTimeout"] = KeysEnum.PoolConnectionTimeout,
                [PoolKeepaliveTimeKey] = KeysEnum.PoolKeepaliveTime,
                ["keepaliveTime"] = KeysEnum.PoolKeepaliveTime,
                [PoolMaxLifetimeKey] = KeysEnum.PoolMaxLifetime,
                ["maxLifetime"] = KeysEnum.PoolMaxLifetime,
                [PoolHousekeepingIntervalKey] = KeysEnum.PoolHousekeepingInterval,
                ["housekeepingInterval"] = KeysEnum.PoolHousekeepingInterval,
                [PoolCreationRetryBackoffKey] = KeysEnum.PoolCreationRetryBackoff,
                ["creationRetryBackoff"] = KeysEnum.PoolCreationRetryBackoff,
                [PoolMaxCreationRetryBackoffKey] = KeysEnum.PoolMaxCreationRetryBackoff,
                ["maxCreationRetryBackoff"] = KeysEnum.PoolMaxCreationRetryBackoff,
                [PoolLeakDetectionThresholdKey] = KeysEnum.PoolLeakDetectionThreshold,
                ["leakDetectionThreshold"] = KeysEnum.PoolLeakDetectionThreshold,
            };
        }

        public ConnectionStringBuilder(string connectionString)
        {
            ConnectionString = connectionString;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                string[] queries = connectionString.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                // timezone and connectionTimezone can not be set in connection string
                bool hasTimezone = false;
                bool hasConnectionTimezone = false;
                foreach (string query in queries)
                {
                    string[] keyValue = query.Split(new char[] { '=' }, 2);
                    if (keyValue.Length != 2)
                    {
                        throw new ArgumentException($"invalid connection param {query}");
                    }

                    var keyword = keyValue[0].Trim();
                    var value = keyValue[1].Trim();
                    KeysEnum index;
                    var exist = KeysDict.TryGetValue(keyword, out index);
                    if (exist)
                    {
                        switch (index)
                        {
                            case KeysEnum.Host:
                                Host = value;
                                break;
                            case KeysEnum.Port:
                                Port = Convert.ToInt32(value);
                                break;
                            case KeysEnum.Database:
                                Database = value;
                                break;
                            case KeysEnum.Username:
                                Username = value;
                                break;
                            case KeysEnum.Password:
                                Password = value;
                                break;
                            case KeysEnum.Protocol:
                                Protocol = value;
                                break;
                            case KeysEnum.Timezone:
                                Timezone = TimeZoneInfo.FindSystemTimeZoneById(value);
                                hasTimezone = true;
                                break;
                            case KeysEnum.ConnTimeout:
                                ConnTimeout = TimeSpan.Parse(value);
                                break;
                            case KeysEnum.ReadTimeout:
                                ReadTimeout = TimeSpan.Parse(value);
                                break;
                            case KeysEnum.WriteTimeout:
                                WriteTimeout = TimeSpan.Parse(value);
                                break;
                            case KeysEnum.Token:
                                Token = value;
                                break;
                            case KeysEnum.UseSSL:
                                UseSSL = Convert.ToBoolean(value);
                                break;
                            case KeysEnum.EnableCompression:
                                EnableCompression = Convert.ToBoolean(value);
                                break;
                            case KeysEnum.AutoReconnect:
                                AutoReconnect = Convert.ToBoolean(value);
                                break;
                            case KeysEnum.ReconnectRetryCount:
                                ReconnectRetryCount = Convert.ToInt32(value);
                                break;
                            case KeysEnum.ReconnectIntervalMs:
                                ReconnectIntervalMs = Convert.ToInt32(value);
                                break;
                            case KeysEnum.ConnectionTimezone:
                                ConnectionTimezone = TimeZoneInfo.FindSystemTimeZoneById(value);
                                hasConnectionTimezone = true;
                                break;
                            case KeysEnum.BearerToken:
                                BearerToken = value;
                                break;
                            case KeysEnum.Pooling:
                                Pooling = Convert.ToBoolean(value);
                                break;
                            case KeysEnum.MinPoolSize:
                                MinPoolSize = Convert.ToInt32(value);
                                break;
                            case KeysEnum.MaxPoolSize:
                                MaxPoolSize = Convert.ToInt32(value);
                                break;
                            case KeysEnum.PoolConnectionTimeout:
                                PoolConnectionTimeout = ParsePoolTimeSpan(value, PoolConnectionTimeoutKey);
                                break;
                            case KeysEnum.PoolKeepaliveTime:
                                PoolKeepaliveTime = ParsePoolTimeSpan(value, PoolKeepaliveTimeKey);
                                break;
                            case KeysEnum.PoolMaxLifetime:
                                PoolMaxLifetime = ParsePoolTimeSpan(value, PoolMaxLifetimeKey);
                                break;
                            case KeysEnum.PoolHousekeepingInterval:
                                PoolHousekeepingInterval = ParsePoolTimeSpan(value, PoolHousekeepingIntervalKey);
                                break;
                            case KeysEnum.PoolCreationRetryBackoff:
                                PoolCreationRetryBackoff = ParsePoolTimeSpan(value, PoolCreationRetryBackoffKey);
                                break;
                            case KeysEnum.PoolMaxCreationRetryBackoff:
                                PoolMaxCreationRetryBackoff = ParsePoolTimeSpan(value, PoolMaxCreationRetryBackoffKey);
                                break;
                            case KeysEnum.PoolLeakDetectionThreshold:
                                PoolLeakDetectionThreshold = ParsePoolTimeSpan(value, PoolLeakDetectionThresholdKey);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(index), index, "get value error");
                        }

                        var canonicalKeyword = KeysList[(int)index];
                        if (!string.Equals(keyword, canonicalKeyword, StringComparison.OrdinalIgnoreCase))
                        {
                            base.Remove(keyword);
                        }
                    }
                }
                if (hasConnectionTimezone && hasTimezone)
                {
                    throw new ArgumentException("connectionTimezone and timezone can not be set at the same time");
                }
            }
        }

        public string Host
        {
            get => _host;
            set => base[HostKey] = _host = value;
        }

        public int Port
        {
            get => _port;
            set
            {
                if (value < 0 || value > ushort.MaxValue)
                {
                    throw new ArgumentException("invalid port value", PortKey);
                }

                base[PortKey] = _port = value;
            }
        }

        public string Database
        {
            get => _db;
            set => base[DatabaseKey] = _db = value;
        }

        public string Username
        {
            get => _user;
            set => base[UsernameKey] = _user = value;
        }

        public string Password
        {
            get => _password;
            set => base[PasswordKey] = _password = value;
        }

        public string Protocol
        {
            get => _protocol;
            set
            {
                if (value != TDengineConstant.ProtocolNative && value != TDengineConstant.ProtocolWebSocket)
                    throw new ArgumentException("invalid protocol value", ProtocolKey);
                base[ProtocolKey] = _protocol = value;
            }
        }


        public TimeZoneInfo Timezone
        {
            get => _timezone;
            set
            {
                base[TimezoneKey] = value.Id;
                _timezone = value;
            }
        }

        public TimeSpan ConnTimeout
        {
            get => _connTimeout;
            set
            {
                base[ConnTimeoutKey] = value.ToString();
                _connTimeout = value;
            }
        }

        public TimeSpan ReadTimeout
        {
            get => _readTimeout;
            set
            {
                base[ReadTimeoutKey] = value.ToString();
                _readTimeout = value;
            }
        }

        public TimeSpan WriteTimeout
        {
            get => _writeTimeout;
            set
            {
                base[WriteTimeoutKey] = value.ToString();
                _writeTimeout = value;
            }
        }

        public string Token
        {
            get => _token;
            set => base[TokenKey] = _token = value;
        }

        public bool UseSSL
        {
            get => _useSSL;
            set => base[UseSSLKey] = _useSSL = value;
        }

        public bool EnableCompression
        {
            get => _enableCompression;
            set => base[EnableCompressionKey] = _enableCompression = value;
        }

        public bool AutoReconnect
        {
            get => _autoReconnect;
            set => base[AutoReconnectKey] = _autoReconnect = value;
        }

        public int ReconnectRetryCount
        {
            get => _reconnectRetryCount;
            set
            {
                if (value < 0)
                    throw new ArgumentException("invalid reconnect retry count value", ReconnectRetryCountKey);
                base[ReconnectRetryCountKey] = _reconnectRetryCount = value;
            }
        }

        public int ReconnectIntervalMs
        {
            get => _reconnectIntervalMs;
            set
            {
                if (value < 0)
                    throw new ArgumentException("invalid reconnect interval value", ReconnectIntervalMsKey);
                base[ReconnectIntervalMsKey] = _reconnectIntervalMs = value;
            }
        }

        public TimeZoneInfo ConnectionTimezone
        {
            get => _connectionTimezone;
            set
            {
#if NET6_0_OR_GREATER
                if (!value.HasIanaId)
                    throw new ArgumentException("invalid connection timezone value, only support IANA ID", ConnectionTimezoneKey);
                base[ConnectionTimezoneKey] = value.Id;
                _connectionTimezone = value;
#else
                throw new ArgumentException("ConnectionTimezone is only supported in .NET 6.0 or later and requires IANA ID",
                    ConnectionTimezoneKey);
#endif
            }
        }
        
        public string BearerToken
        {
            get => _bearerToken;
            set => base[BearerTokenKey] = _bearerToken = value;
        }

        public bool Pooling
        {
            get => _pooling;
            set => base[PoolingKey] = _pooling = value;
        }

        public int MinPoolSize
        {
            get => _minPoolSize;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentException("invalid min pool size value", MinPoolSizeKey);
                }

                base[MinPoolSizeKey] = _minPoolSize = value;
            }
        }

        public int MaxPoolSize
        {
            get => _maxPoolSize;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentException("invalid max pool size value", MaxPoolSizeKey);
                }

                base[MaxPoolSizeKey] = _maxPoolSize = value;
            }
        }

        public TimeSpan PoolConnectionTimeout
        {
            get => _poolConnectionTimeout;
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool connection timeout value", PoolConnectionTimeoutKey);
                }

                base[PoolConnectionTimeoutKey] = value.ToString();
                _poolConnectionTimeout = value;
            }
        }

        public TimeSpan PoolKeepaliveTime
        {
            get => _poolKeepaliveTime;
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool keepalive time value", PoolKeepaliveTimeKey);
                }

                base[PoolKeepaliveTimeKey] = value.ToString();
                _poolKeepaliveTime = value;
            }
        }

        public TimeSpan PoolMaxLifetime
        {
            get => _poolMaxLifetime;
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool max lifetime value", PoolMaxLifetimeKey);
                }

                base[PoolMaxLifetimeKey] = value.ToString();
                _poolMaxLifetime = value;
            }
        }

        public TimeSpan PoolHousekeepingInterval
        {
            get => _poolHousekeepingInterval;
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool housekeeping interval value",
                        PoolHousekeepingIntervalKey);
                }

                base[PoolHousekeepingIntervalKey] = value.ToString();
                _poolHousekeepingInterval = value;
            }
        }

        public TimeSpan PoolCreationRetryBackoff
        {
            get => _poolCreationRetryBackoff;
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool creation retry backoff value",
                        PoolCreationRetryBackoffKey);
                }

                base[PoolCreationRetryBackoffKey] = value.ToString();
                _poolCreationRetryBackoff = value;
            }
        }

        public TimeSpan PoolMaxCreationRetryBackoff
        {
            get => _poolMaxCreationRetryBackoff;
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool max creation retry backoff value",
                        PoolMaxCreationRetryBackoffKey);
                }

                base[PoolMaxCreationRetryBackoffKey] = value.ToString();
                _poolMaxCreationRetryBackoff = value;
            }
        }

        public TimeSpan PoolLeakDetectionThreshold
        {
            get => _poolLeakDetectionThreshold;
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentException("invalid pool leak detection threshold value",
                        PoolLeakDetectionThresholdKey);
                }

                base[PoolLeakDetectionThresholdKey] = value.ToString();
                _poolLeakDetectionThreshold = value;
            }
        }


        public override ICollection Keys => new ReadOnlyCollection<string>((string[])KeysList);

        public override ICollection Values
        {
            get
            {
                var values = new object[KeysList.Count];
                for (int i = 0; i < KeysList.Count; i++)
                {
                    values[i] = GetAt((KeysEnum)i);
                }

                return new ReadOnlyCollection<object>(values);
            }
        }

        private object GetAt(KeysEnum index)
        {
            switch (index)
            {
                case KeysEnum.Host:
                    return Host;
                case KeysEnum.Port:
                    return Port;
                case KeysEnum.Database:
                    return Database;
                case KeysEnum.Username:
                    return Username;
                case KeysEnum.Password:
                    return Password;
                case KeysEnum.Protocol:
                    return Protocol;
                case KeysEnum.Timezone:
                    return Timezone;
                case KeysEnum.ConnTimeout:
                    return ConnTimeout;
                case KeysEnum.ReadTimeout:
                    return ReadTimeout;
                case KeysEnum.WriteTimeout:
                    return WriteTimeout;
                case KeysEnum.Token:
                    return Token;
                case KeysEnum.UseSSL:
                    return UseSSL;
                case KeysEnum.EnableCompression:
                    return EnableCompression;
                case KeysEnum.AutoReconnect:
                    return AutoReconnect;
                case KeysEnum.ReconnectRetryCount:
                    return ReconnectRetryCount;
                case KeysEnum.ReconnectIntervalMs:
                    return ReconnectIntervalMs;
                case KeysEnum.ConnectionTimezone:
                    return ConnectionTimezone;
                case KeysEnum.BearerToken:
                    return BearerToken;
                case KeysEnum.Pooling:
                    return Pooling;
                case KeysEnum.MinPoolSize:
                    return MinPoolSize;
                case KeysEnum.MaxPoolSize:
                    return MaxPoolSize;
                case KeysEnum.PoolConnectionTimeout:
                    return PoolConnectionTimeout;
                case KeysEnum.PoolKeepaliveTime:
                    return PoolKeepaliveTime;
                case KeysEnum.PoolMaxLifetime:
                    return PoolMaxLifetime;
                case KeysEnum.PoolHousekeepingInterval:
                    return PoolHousekeepingInterval;
                case KeysEnum.PoolCreationRetryBackoff:
                    return PoolCreationRetryBackoff;
                case KeysEnum.PoolMaxCreationRetryBackoff:
                    return PoolMaxCreationRetryBackoff;
                case KeysEnum.PoolLeakDetectionThreshold:
                    return PoolLeakDetectionThreshold;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index), index, "get value error");
            }
        }

        public override bool TryGetValue(string keyword, out object value)
        {
            if (!KeysDict.TryGetValue(keyword, out var index))
            {
                value = null;

                return false;
            }

            value = GetAt(index);

            return true;
        }

        private void Reset(KeysEnum index)
        {
            switch (index)
            {
                case KeysEnum.Host:
                    _host = string.Empty;
                    return;
                case KeysEnum.Port:
                    _port = 0;
                    return;
                case KeysEnum.Database:
                    _db = string.Empty;
                    return;
                case KeysEnum.Username:
                    _user = string.Empty;
                    return;
                case KeysEnum.Password:
                    _password = string.Empty;
                    return;
                case KeysEnum.Protocol:
                    _protocol = TDengineConstant.ProtocolNative;
                    return;
                case KeysEnum.Timezone:
                    _timezone = TimeZoneInfo.Local;
                    return;
                case KeysEnum.ConnTimeout:
                    _connTimeout = TimeSpan.Zero;
                    return;
                case KeysEnum.ReadTimeout:
                    _readTimeout = TimeSpan.Zero;
                    return;
                case KeysEnum.WriteTimeout:
                    _writeTimeout = TimeSpan.Zero;
                    return;
                case KeysEnum.Token:
                    _token = string.Empty;
                    return;
                case KeysEnum.UseSSL:
                    _useSSL = false;
                    return;
                case KeysEnum.EnableCompression:
                    _enableCompression = false;
                    return;
                case KeysEnum.AutoReconnect:
                    _autoReconnect = false;
                    return;
                case KeysEnum.ReconnectRetryCount:
                    _reconnectRetryCount = 3;
                    return;
                case KeysEnum.ReconnectIntervalMs:
                    _reconnectIntervalMs = 2000;
                    return;
                case KeysEnum.ConnectionTimezone:
                    _connectionTimezone = null;
                    return;
                case KeysEnum.BearerToken:
                    _bearerToken = string.Empty;
                    return;
                case KeysEnum.Pooling:
                    _pooling = false;
                    return;
                case KeysEnum.MinPoolSize:
                    _minPoolSize = 0;
                    return;
                case KeysEnum.MaxPoolSize:
                    _maxPoolSize = 10;
                    return;
                case KeysEnum.PoolConnectionTimeout:
                    _poolConnectionTimeout = TimeSpan.FromSeconds(30);
                    return;
                case KeysEnum.PoolKeepaliveTime:
                    _poolKeepaliveTime = TimeSpan.FromMinutes(2);
                    return;
                case KeysEnum.PoolMaxLifetime:
                    _poolMaxLifetime = TimeSpan.FromMinutes(30);
                    return;
                case KeysEnum.PoolHousekeepingInterval:
                    _poolHousekeepingInterval = TimeSpan.FromSeconds(30);
                    return;
                case KeysEnum.PoolCreationRetryBackoff:
                    _poolCreationRetryBackoff = TimeSpan.FromMilliseconds(100);
                    return;
                case KeysEnum.PoolMaxCreationRetryBackoff:
                    _poolMaxCreationRetryBackoff = TimeSpan.FromSeconds(2);
                    return;
                case KeysEnum.PoolLeakDetectionThreshold:
                    _poolLeakDetectionThreshold = TimeSpan.Zero;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index), index, null);
            }
        }

        public override bool Remove(string keyword)
        {
            if (!KeysDict.TryGetValue(keyword, out var index)
                || !base.Remove(KeysList[(int)index]))
            {
                return false;
            }

            Reset(index);

            return true;
        }

        public override void Clear()
        {
            base.Clear();

            for (var i = 0; i < KeysList.Count; i++)
            {
                Reset((KeysEnum)i);
            }
        }

        public void DefaultNative()
        {
            Port = 6030;
            Host = "localhost";
            Protocol = TDengineConstant.ProtocolNative;
        }


        public void DefaultWebSocket()
        {
            Port = 6041;
            Host = "localhost";
            Protocol = TDengineConstant.ProtocolWebSocket;
        }

        internal IReadOnlyList<FailoverAddress> GetFailoverAddresses()
        {
            var endpoints = new List<FailoverAddress>();
            var deduplicatedCacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hostValue = Host ?? string.Empty;
            var hostSegments = hostValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (hostSegments.Length == 0)
            {
                throw new ArgumentException("host value cannot be empty", HostKey);
            }

            var isMultiHost = hostSegments.Length > 1;
            for (var i = 0; i < hostSegments.Length; i++)
            {
                HostEndpointParser.ParseHostEndpoint(hostSegments[i], HostKey, out var endpointHost,
                    out var endpointPort, allowBareIpv6: !isMultiHost);
                var resolvedPort = ResolvePort(endpointPort);
                var cacheKey = HostEndpointParser.BuildFailoverCacheKey(Protocol, UseSSL, endpointHost, resolvedPort);
                if (!deduplicatedCacheKeys.Add(cacheKey))
                {
                    continue;
                }

                endpoints.Add(new FailoverAddress(endpointHost, resolvedPort, cacheKey));
            }

            if (endpoints.Count == 0)
            {
                throw new ArgumentException("invalid host value", HostKey);
            }

            return endpoints;
        }

        private int ResolvePort(int endpointPort)
        {
            if (endpointPort > 0)
            {
                return endpointPort;
            }

            if (Port > 0)
            {
                return Port;
            }

            if (Protocol != TDengineConstant.ProtocolWebSocket)
            {
                return 0;
            }

            return UseSSL ? 443 : 6041;
        }

        public TimeZoneInfo GetTimeZone()
        {
            if (ConnectionTimezone != null)
            {
                return ConnectionTimezone;
            }

            return Timezone;
        }

        internal WSClientAsyncPoolOptions CreateWebSocketAsyncPoolOptions()
        {
            return new WSClientAsyncPoolOptions
            {
                MinIdle = MinPoolSize,
                MaximumPoolSize = MaxPoolSize,
                ConnectionTimeout = PoolConnectionTimeout,
                KeepaliveTime = PoolKeepaliveTime,
                MaxLifetime = PoolMaxLifetime,
                HousekeepingInterval = PoolHousekeepingInterval,
                CreationRetryBackoff = PoolCreationRetryBackoff,
                MaxCreationRetryBackoff = PoolMaxCreationRetryBackoff,
                LeakDetectionThreshold = PoolLeakDetectionThreshold
            };
        }

        private static TimeSpan ParsePoolTimeSpan(string value, string keyword)
        {
            if (value == null)
            {
                throw new ArgumentNullException(keyword);
            }

            var text = value.Trim();
            if (text.Length == 0)
            {
                throw new ArgumentException("invalid pool timespan value", keyword);
            }

            TimeSpan result;
            if (TryParsePoolTimeSpanWithSuffix(text, "ms", TimeSpan.FromMilliseconds, out result) ||
                TryParsePoolTimeSpanWithSuffix(text, "s", TimeSpan.FromSeconds, out result) ||
                TryParsePoolTimeSpanWithSuffix(text, "m", TimeSpan.FromMinutes, out result) ||
                TryParsePoolTimeSpanWithSuffix(text, "h", TimeSpan.FromHours, out result))
            {
                return result;
            }

            double milliseconds;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }

            throw new ArgumentException("invalid pool timespan value", keyword);
        }

        private static bool TryParsePoolTimeSpanWithSuffix(string value, string suffix,
            Func<double, TimeSpan> factory, out TimeSpan result)
        {
            if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = default(TimeSpan);
                return false;
            }

            var number = value.Substring(0, value.Length - suffix.Length).Trim();
            double parsed;
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                result = default(TimeSpan);
                return false;
            }

            result = factory(parsed);
            return true;
        }
    }
}
