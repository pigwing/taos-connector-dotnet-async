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
        private const string AdapterHAKey = "adapterHA";
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
            AdapterHA,
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
        private bool _timezoneExplicit;
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
        private bool _adapterHA = false;
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
            list[(int)KeysEnum.AdapterHA] = AdapterHAKey;
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
                [AdapterHAKey] = KeysEnum.AdapterHA,
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
            ValidatePoolSizes();
        }

        public new string ConnectionString
        {
            get => base.ConnectionString;
            set
            {
                base.ConnectionString = value;
                ValidatePoolSizes();
            }
        }

        public override object this[string keyword]
        {
            get
            {
                if (!KeysDict.TryGetValue(keyword, out var index))
                {
                    return base[keyword];
                }

                return GetAt(index);
            }
            set
            {
                if (!KeysDict.TryGetValue(keyword, out var index))
                {
                    base[keyword] = value;
                    return;
                }

                if (value == null)
                {
                    Remove(keyword);
                    return;
                }

                SetAt(index, value);
                var canonicalKeyword = KeysList[(int)index];
                if (!string.Equals(keyword, canonicalKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    base.Remove(keyword);
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
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                if (_connectionTimezone != null)
                {
                    throw new ArgumentException("connectionTimezone and timezone can not be set at the same time",
                        TimezoneKey);
                }

                base[TimezoneKey] = value.Id;
                _timezone = value;
                _timezoneExplicit = true;
            }
        }

        public TimeSpan ConnTimeout
        {
            get => _connTimeout;
            set
            {
                ValidateNetworkTimeout(value, ConnTimeoutKey);
                base[ConnTimeoutKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _connTimeout = value;
            }
        }

        public TimeSpan ReadTimeout
        {
            get => _readTimeout;
            set
            {
                ValidateNetworkTimeout(value, ReadTimeoutKey);
                base[ReadTimeoutKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _readTimeout = value;
            }
        }

        public TimeSpan WriteTimeout
        {
            get => _writeTimeout;
            set
            {
                ValidateNetworkTimeout(value, WriteTimeoutKey);
                base[WriteTimeoutKey] = value.ToString("c", CultureInfo.InvariantCulture);
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
                if (value == null)
                {
                    base.Remove(ConnectionTimezoneKey);
                    _connectionTimezone = null;
                    return;
                }

#if NET6_0_OR_GREATER
                if (_timezoneExplicit)
                {
                    throw new ArgumentException("connectionTimezone and timezone can not be set at the same time",
                        ConnectionTimezoneKey);
                }

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

        public bool AdapterHA
        {
            get => _adapterHA;
            set => base[AdapterHAKey] = _adapterHA = value;
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
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, false))
                {
                    throw new ArgumentException("invalid pool connection timeout value", PoolConnectionTimeoutKey);
                }

                base[PoolConnectionTimeoutKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolConnectionTimeout = value;
            }
        }

        public TimeSpan PoolKeepaliveTime
        {
            get => _poolKeepaliveTime;
            set
            {
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, true))
                {
                    throw new ArgumentException("invalid pool keepalive time value", PoolKeepaliveTimeKey);
                }

                base[PoolKeepaliveTimeKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolKeepaliveTime = value;
            }
        }

        public TimeSpan PoolMaxLifetime
        {
            get => _poolMaxLifetime;
            set
            {
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, true))
                {
                    throw new ArgumentException("invalid pool max lifetime value", PoolMaxLifetimeKey);
                }

                base[PoolMaxLifetimeKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolMaxLifetime = value;
            }
        }

        public TimeSpan PoolHousekeepingInterval
        {
            get => _poolHousekeepingInterval;
            set
            {
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, false))
                {
                    throw new ArgumentException("invalid pool housekeeping interval value",
                        PoolHousekeepingIntervalKey);
                }

                base[PoolHousekeepingIntervalKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolHousekeepingInterval = value;
            }
        }

        public TimeSpan PoolCreationRetryBackoff
        {
            get => _poolCreationRetryBackoff;
            set
            {
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, true))
                {
                    throw new ArgumentException("invalid pool creation retry backoff value",
                        PoolCreationRetryBackoffKey);
                }

                base[PoolCreationRetryBackoffKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolCreationRetryBackoff = value;
            }
        }

        public TimeSpan PoolMaxCreationRetryBackoff
        {
            get => _poolMaxCreationRetryBackoff;
            set
            {
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, true))
                {
                    throw new ArgumentException("invalid pool max creation retry backoff value",
                        PoolMaxCreationRetryBackoffKey);
                }

                base[PoolMaxCreationRetryBackoffKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolMaxCreationRetryBackoff = value;
            }
        }

        public TimeSpan PoolLeakDetectionThreshold
        {
            get => _poolLeakDetectionThreshold;
            set
            {
                if (!TimeoutHelper.IsSupportedTimerTimeout(value, true))
                {
                    throw new ArgumentException("invalid pool leak detection threshold value",
                        PoolLeakDetectionThresholdKey);
                }

                base[PoolLeakDetectionThresholdKey] = value.ToString("c", CultureInfo.InvariantCulture);
                _poolLeakDetectionThreshold = value;
            }
        }


        public override ICollection Keys
        {
            get
            {
                var keys = new List<string>(KeysList.Count + base.Count);
                keys.AddRange(KeysList);
                foreach (var keyObject in base.Keys)
                {
                    var key = Convert.ToString(keyObject, CultureInfo.InvariantCulture);
                    if (!KeysDict.ContainsKey(key))
                    {
                        keys.Add(key);
                    }
                }

                return new ReadOnlyCollection<string>(keys);
            }
        }

        public override ICollection Values
        {
            get
            {
                var values = new List<object>(KeysList.Count + base.Count);
                for (int i = 0; i < KeysList.Count; i++)
                {
                    values.Add(GetAt((KeysEnum)i));
                }

                foreach (var keyObject in base.Keys)
                {
                    var key = Convert.ToString(keyObject, CultureInfo.InvariantCulture);
                    if (!KeysDict.ContainsKey(key))
                    {
                        values.Add(base[key]);
                    }
                }

                return new ReadOnlyCollection<object>(values);
            }
        }

        private void SetAt(KeysEnum index, object value)
        {
            switch (index)
            {
                case KeysEnum.Host:
                    Host = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Port:
                    Port = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Database:
                    Database = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Username:
                    Username = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Password:
                    Password = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Protocol:
                    Protocol = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Timezone:
                    Timezone = value as TimeZoneInfo ?? TimeZoneInfo.FindSystemTimeZoneById(
                        Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case KeysEnum.ConnTimeout:
                    ConnTimeout = value is TimeSpan connTimeout
                        ? connTimeout
                        : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.ReadTimeout:
                    ReadTimeout = value is TimeSpan readTimeout
                        ? readTimeout
                        : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.WriteTimeout:
                    WriteTimeout = value is TimeSpan writeTimeout
                        ? writeTimeout
                        : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Token:
                    Token = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.UseSSL:
                    UseSSL = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.EnableCompression:
                    EnableCompression = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.AutoReconnect:
                    AutoReconnect = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.ReconnectRetryCount:
                    ReconnectRetryCount = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.ReconnectIntervalMs:
                    ReconnectIntervalMs = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.ConnectionTimezone:
                    ConnectionTimezone = value as TimeZoneInfo ?? TimeZoneInfo.FindSystemTimeZoneById(
                        Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case KeysEnum.BearerToken:
                    BearerToken = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.AdapterHA:
                    AdapterHA = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.Pooling:
                    Pooling = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.MinPoolSize:
                    MinPoolSize = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.MaxPoolSize:
                    MaxPoolSize = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case KeysEnum.PoolConnectionTimeout:
                    PoolConnectionTimeout = GetPoolTimeSpan(value, PoolConnectionTimeoutKey);
                    return;
                case KeysEnum.PoolKeepaliveTime:
                    PoolKeepaliveTime = GetPoolTimeSpan(value, PoolKeepaliveTimeKey);
                    return;
                case KeysEnum.PoolMaxLifetime:
                    PoolMaxLifetime = GetPoolTimeSpan(value, PoolMaxLifetimeKey);
                    return;
                case KeysEnum.PoolHousekeepingInterval:
                    PoolHousekeepingInterval = GetPoolTimeSpan(value, PoolHousekeepingIntervalKey);
                    return;
                case KeysEnum.PoolCreationRetryBackoff:
                    PoolCreationRetryBackoff = GetPoolTimeSpan(value, PoolCreationRetryBackoffKey);
                    return;
                case KeysEnum.PoolMaxCreationRetryBackoff:
                    PoolMaxCreationRetryBackoff = GetPoolTimeSpan(value, PoolMaxCreationRetryBackoffKey);
                    return;
                case KeysEnum.PoolLeakDetectionThreshold:
                    PoolLeakDetectionThreshold = GetPoolTimeSpan(value, PoolLeakDetectionThresholdKey);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index), index, "set value error");
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
                case KeysEnum.AdapterHA:
                    return AdapterHA;
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
                return base.TryGetValue(keyword, out value);
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
                    _timezoneExplicit = false;
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
                case KeysEnum.AdapterHA:
                    _adapterHA = false;
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
            if (!KeysDict.TryGetValue(keyword, out var index))
            {
                return base.Remove(keyword);
            }

            var canonicalKeyword = KeysList[(int)index];
            var removed = base.Remove(canonicalKeyword);
            if (!string.Equals(keyword, canonicalKeyword, StringComparison.OrdinalIgnoreCase))
            {
                removed |= base.Remove(keyword);
            }

            if (!removed)
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
            var hostSegments = hostValue.Split(new[] { ',' }, StringSplitOptions.None);

            if (hostSegments.Length == 0 || Array.Exists(hostSegments, string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("host value contains an empty endpoint", HostKey);
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
            ValidatePoolSizes();
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

        internal ConnectionStringBuilder CreateSnapshot()
        {
            ValidatePoolSizes();
            var snapshot = new ConnectionStringBuilder(string.Empty)
            {
                Host = Host,
                Port = Port,
                Database = Database,
                Username = Username,
                Password = Password,
                Protocol = Protocol,
                ConnTimeout = ConnTimeout,
                ReadTimeout = ReadTimeout,
                WriteTimeout = WriteTimeout,
                Token = Token,
                UseSSL = UseSSL,
                EnableCompression = EnableCompression,
                AutoReconnect = AutoReconnect,
                ReconnectRetryCount = ReconnectRetryCount,
                ReconnectIntervalMs = ReconnectIntervalMs,
                BearerToken = BearerToken,
                AdapterHA = AdapterHA,
                Pooling = Pooling,
                MinPoolSize = MinPoolSize,
                MaxPoolSize = MaxPoolSize,
                PoolConnectionTimeout = PoolConnectionTimeout,
                PoolKeepaliveTime = PoolKeepaliveTime,
                PoolMaxLifetime = PoolMaxLifetime,
                PoolHousekeepingInterval = PoolHousekeepingInterval,
                PoolCreationRetryBackoff = PoolCreationRetryBackoff,
                PoolMaxCreationRetryBackoff = PoolMaxCreationRetryBackoff,
                PoolLeakDetectionThreshold = PoolLeakDetectionThreshold
            };

            if (_timezoneExplicit)
            {
                snapshot.Timezone = Timezone;
            }

            if (ConnectionTimezone != null)
            {
                snapshot.ConnectionTimezone = ConnectionTimezone;
            }

            foreach (var keyObject in base.Keys)
            {
                var key = Convert.ToString(keyObject, CultureInfo.InvariantCulture);
                if (!KeysDict.ContainsKey(key))
                {
                    snapshot[key] = base[key];
                }
            }

            return snapshot;
        }

        private void ValidatePoolSizes()
        {
            if (_minPoolSize > _maxPoolSize)
            {
                throw new ArgumentException("minPoolSize cannot be greater than maxPoolSize", MinPoolSizeKey);
            }
        }

        private static void ValidateNetworkTimeout(TimeSpan value, string keyword)
        {
            if (!TimeoutHelper.IsSupportedTimerTimeout(value, true))
            {
                throw new ArgumentException("invalid timeout value", keyword);
            }
        }

        private static TimeSpan GetPoolTimeSpan(object value, string keyword)
        {
            return value is TimeSpan timeSpan
                ? timeSpan
                : ParsePoolTimeSpan(Convert.ToString(value, CultureInfo.InvariantCulture), keyword);
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
            if (TryParsePoolTimeSpanWithSuffix(text, "ms", TimeSpan.FromMilliseconds, keyword, out result) ||
                TryParsePoolTimeSpanWithSuffix(text, "s", TimeSpan.FromSeconds, keyword, out result) ||
                TryParsePoolTimeSpanWithSuffix(text, "m", TimeSpan.FromMinutes, keyword, out result) ||
                TryParsePoolTimeSpanWithSuffix(text, "h", TimeSpan.FromHours, keyword, out result))
            {
                return result;
            }

            double milliseconds;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
            {
                if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
                {
                    throw new ArgumentException("invalid pool timespan value", keyword);
                }

                try
                {
                    return TimeSpan.FromMilliseconds(milliseconds);
                }
                catch (Exception e) when (e is ArgumentException || e is OverflowException)
                {
                    throw new ArgumentException("invalid pool timespan value", keyword, e);
                }
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }

            throw new ArgumentException("invalid pool timespan value", keyword);
        }

        private static bool TryParsePoolTimeSpanWithSuffix(string value, string suffix,
            Func<double, TimeSpan> factory, string keyword, out TimeSpan result)
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

            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                result = default(TimeSpan);
                return false;
            }

            try
            {
                result = factory(parsed);
            }
            catch (Exception e) when (e is ArgumentException || e is OverflowException)
            {
                throw new ArgumentException("invalid pool timespan value", keyword, e);
            }

            return true;
        }
    }
}
