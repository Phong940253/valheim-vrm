using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using ServerSync;
using UnityEngine;

namespace ValheimVRM
{
    /// <summary>
    /// Binds every public field of the Settings containers to BepInEx ConfigEntry objects,
    /// making them visible/editable in the BepInEx Configuration Manager (F1) and synced
    /// between server and clients through ServerSync.
    ///
    /// The .cfg file is the source of truth. Legacy .txt settings are imported exactly once
    /// (per section, tracked by the CfgMigrated marker) and ignored afterwards.
    ///
    /// Flow: ConfigEntry (cfg) --SettingChanged--> container field -> OnUpdate (live apply).
    ///       container (legacy txt load) --PushSectionToContainer--> ConfigEntry (cfg).
    /// </summary>
    public static class ConfigBindings
    {
        private static ConfigFile config;
        private static ConfigSync configSync;
        private static readonly Dictionary<string, ConfigEntryBase> entries = new Dictionary<string, ConfigEntryBase>();
        private static bool vectorConvertersRegistered;

        private static readonly Regex Vector2Regex = new Regex(@"\(\s*(?<x>[^,]*?)\s*,\s*(?<y>[^,]*?)\s*\)");
        private static readonly Regex Vector3Regex = new Regex(@"\(\s*(?<x>[^,]*?)\s*,\s*(?<y>[^,]*?)\s*,\s*(?<z>[^,]*?)\s*\)");
        private static readonly Regex Vector4Regex = new Regex(@"\(\s*(?<x>[^,]*?)\s*,\s*(?<y>[^,]*?)\s*,\s*(?<z>[^,]*?)\s*,\s*(?<w>[^,]*?)\s*\)");

        public static bool Initialized => config != null;

        public static void Init(ConfigFile configFile)
        {
            config = configFile;
            RegisterVectorConverters();

            configSync = new ConfigSync(MainPlugin.PluginGuid)
            {
                DisplayName = MainPlugin.PluginName,
                CurrentVersion = MainPlugin.PluginVersion,
                MinimumRequiredVersion = MainPlugin.PluginVersion,
                ModRequired = true
            };

            configSync.AddLockingConfigEntry(config.Bind(
                "Internal", "ConfigLocked", false,
                "If enabled, the configuration is locked to the server's values. Locked entries become read-only."));
        }

        public static void BindGlobal()
        {
            if (config == null) return;
            BindContainer(Settings.globalSettings, "Global", Path.Combine(Settings.ValheimVRMDir, "global_settings.txt"));
        }

        public static void BindCharacter(string playerName, string legacyPath)
        {
            if (config == null) return;
            var container = Settings.GetSettings(playerName);
            if (container == null) return;
            BindContainer(container, playerName, legacyPath);
        }

        /// <summary>Re-applies the .cfg values (source of truth) to the global container.</summary>
        public static void ReloadGlobal()
        {
            if (config == null) return;
            PushSectionToContainer(Settings.globalSettings, "Global");
        }

        /// <summary>Re-applies the .cfg values (source of truth) to a character's container.</summary>
        public static void ReloadCharacter(string playerName)
        {
            if (config == null) return;
            var container = Settings.GetSettings(playerName);
            if (container == null) return;
            PushSectionToContainer(container, playerName);
        }

        /// <summary>
        /// Binds all public fields of a container to config entries, imports legacy .txt values
        /// once, then pushes the config (source of truth) into the container.
        /// </summary>
        private static void BindContainer(Settings.Container container, string section, string legacyPath)
        {
            foreach (var field in container.GetType().GetFields())
            {
                if (field.GetCustomAttribute(typeof(NonSerializedAttribute)) != null) continue;
                BindField(container, field, section);
            }

            EnsureMigrated(container, section, legacyPath);
            PushSectionToContainer(container, section);
        }

        private static void BindField(Settings.Container container, FieldInfo field, string section)
        {
            var key = SectionKey(section, field.Name);
            if (entries.ContainsKey(key)) return;

            var method = typeof(ConfigBindings).GetMethod(nameof(BindFieldGeneric), BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                method.MakeGenericMethod(field.FieldType).Invoke(null, new object[] { container, field, section });
            }
            catch (Exception e)
            {
                Debug.LogError("[ValheimVRM] failed to bind config field " + section + "." + field.Name + ": " + e);
            }
        }

        private static void BindFieldGeneric<T>(Settings.Container container, FieldInfo field, string section) where T : IEquatable<T>
        {
            var key = SectionKey(section, field.Name);
            var description = new ConfigDescription("ValheimVRM: " + field.Name, RangeFor(field.Name, field.FieldType));
            var entry = config.Bind<T>(section, field.Name, (T)field.GetValue(container), description);
            configSync.AddConfigEntry(entry);
            entry.SettingChanged += (sender, e) => ApplyEntryToContainer(container, field, entry);
            entries[key] = entry;
        }

        /// <summary>
        /// Pushes the config entries (source of truth) into the container fields and fires OnUpdate.
        /// </summary>
        private static void PushSectionToContainer(Settings.Container container, string section)
        {
            var changes = new Dictionary<string, object>();
            foreach (var field in container.GetType().GetFields())
            {
                if (field.GetCustomAttribute(typeof(NonSerializedAttribute)) != null) continue;
                if (!entries.TryGetValue(SectionKey(section, field.Name), out var entry)) continue;

                var old = field.GetValue(container);
                var value = entry.BoxedValue;
                if (!Equals(old, value)) changes[field.Name] = old;
                field.SetValue(container, value);
            }

            if (changes.Count > 0) container.OnUpdate(changes);
        }

        /// <summary>
        /// Applies a ConfigEntry change (user edit in Configuration Manager or server sync) to the
        /// container field and fires OnUpdate so the game updates live.
        /// </summary>
        private static void ApplyEntryToContainer(Settings.Container container, FieldInfo field, ConfigEntryBase entry)
        {
            var old = field.GetValue(container);
            var value = entry.BoxedValue;
            if (Equals(old, value)) return;
            field.SetValue(container, value);
            container.OnUpdate(new Dictionary<string, object> { { field.Name, old } });
        }

        /// <summary>
        /// One-time import of the legacy .txt settings into the config entries. The CfgMigrated
        /// marker records that the import happened, after which the .cfg file is authoritative.
        /// </summary>
        private static void EnsureMigrated(Settings.Container container, string section, string legacyPath)
        {
            var markerKey = SectionKey(section, "CfgMigrated");
            ConfigEntryBase marker;
            if (!entries.TryGetValue(markerKey, out marker))
            {
                marker = config.Bind(section, "CfgMigrated", false, "Internal: set to true once legacy .txt settings were imported.");
                entries[markerKey] = marker;
            }

            if ((bool)marker.BoxedValue) return;
            if (legacyPath != null && File.Exists(legacyPath))
            {
                try
                {
                    // Suppress ServerSync broadcasts while importing (they could echo stale values).
                    var wasProcessing = ConfigSync.ProcessingServerUpdate;
                    ConfigSync.ProcessingServerUpdate = true;
                    try
                    {
                        var data = new Dictionary<string, string>();
                        foreach (var kv in Settings.ParseSettings(File.ReadAllLines(legacyPath))) data[kv.Key] = kv.Value;

                        foreach (var field in container.GetType().GetFields())
                        {
                            if (field.GetCustomAttribute(typeof(NonSerializedAttribute)) != null) continue;
                            if (!data.TryGetValue(field.Name, out var valueStr)) continue;
                            if (!entries.TryGetValue(SectionKey(section, field.Name), out var entry)) continue;

                            object value;
                            if (TryParse(field.FieldType, valueStr, out value))
                            {
                                entry.BoxedValue = value;
                            }
                            else
                            {
                                Debug.LogWarning("[ValheimVRM] could not migrate legacy value: " + section + "." + field.Name + "=" + valueStr);
                            }
                        }
                    }
                    finally
                    {
                        ConfigSync.ProcessingServerUpdate = wasProcessing;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[ValheimVRM] legacy settings migration failed for " + legacyPath + ": " + e);
                }
            }

            marker.BoxedValue = true;
        }

        private static bool TryParse(Type type, string str, out object value)
        {
            value = null;
            try
            {
                if (type == typeof(float))
                {
                    value = float.Parse(str, CultureInfo.InvariantCulture);
                    return true;
                }
                if (type == typeof(bool))
                {
                    value = bool.Parse(str);
                    return true;
                }
                if (type == typeof(int))
                {
                    value = int.Parse(str, CultureInfo.InvariantCulture);
                    return true;
                }
                if (type == typeof(string))
                {
                    value = str;
                    return true;
                }
                if (type == typeof(Vector2))
                {
                    var m = Vector2Regex.Match(str);
                    if (m.Success)
                    {
                        value = new Vector2(ParseF(m, "x"), ParseF(m, "y"));
                        return true;
                    }
                    return false;
                }
                if (type == typeof(Vector3))
                {
                    var m = Vector3Regex.Match(str);
                    if (m.Success)
                    {
                        value = new Vector3(ParseF(m, "x"), ParseF(m, "y"), ParseF(m, "z"));
                        return true;
                    }
                    return false;
                }
                if (type == typeof(Vector4))
                {
                    var m = Vector4Regex.Match(str);
                    if (m.Success)
                    {
                        value = new Vector4(ParseF(m, "x"), ParseF(m, "y"), ParseF(m, "z"), ParseF(m, "w"));
                        return true;
                    }
                    return false;
                }
                if (type.IsEnum)
                {
                    value = Enum.ToObject(type, str);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        private static float ParseF(Match m, string group) => float.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

        private static void RegisterVectorConverters()
        {
            if (vectorConvertersRegistered) return;
            vectorConvertersRegistered = true;

            TomlTypeConverter.AddConverter(typeof(Vector2), new TypeConverter
            {
                ConvertToString = (value, _) => SerializeVector2(value),
                ConvertToObject = (value, _) => DeserializeVector2(value)
            });
            TomlTypeConverter.AddConverter(typeof(Vector3), new TypeConverter
            {
                ConvertToString = (value, _) => SerializeVector3(value),
                ConvertToObject = (value, _) => DeserializeVector3(value)
            });
            TomlTypeConverter.AddConverter(typeof(Vector4), new TypeConverter
            {
                ConvertToString = (value, _) => SerializeVector4(value),
                ConvertToObject = (value, _) => DeserializeVector4(value)
            });
        }

        private static string SerializeVector2(object value)
        {
            var v = (Vector2)value;
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1})", v.x, v.y);
        }

        private static string SerializeVector3(object value)
        {
            var v = (Vector3)value;
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", v.x, v.y, v.z);
        }

        private static string SerializeVector4(object value)
        {
            var v = (Vector4)value;
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2}, {3})", v.x, v.y, v.z, v.w);
        }

        private static object DeserializeVector2(string value)
        {
            var m = Vector2Regex.Match(value);
            if (!m.Success) throw new FormatException("invalid Vector2: " + value);
            return new Vector2(ParseF(m, "x"), ParseF(m, "y"));
        }

        private static object DeserializeVector3(string value)
        {
            var m = Vector3Regex.Match(value);
            if (!m.Success) throw new FormatException("invalid Vector3: " + value);
            return new Vector3(ParseF(m, "x"), ParseF(m, "y"), ParseF(m, "z"));
        }

        private static object DeserializeVector4(string value)
        {
            var m = Vector4Regex.Match(value);
            if (!m.Success) throw new FormatException("invalid Vector4: " + value);
            return new Vector4(ParseF(m, "x"), ParseF(m, "y"), ParseF(m, "z"), ParseF(m, "w"));
        }

        /// <summary>
        /// Generous slider ranges for the Configuration Manager based on field names.
        /// </summary>
        private static AcceptableValueBase RangeFor(string name, Type type)
        {
            if (type == typeof(float))
            {
                if (name.Contains("Offset")) return new AcceptableValueRange<float>(-5f, 5f);
                if (name.Contains("Rate")) return new AcceptableValueRange<float>(0f, 200f);
                if (name.Contains("Brightness")) return new AcceptableValueRange<float>(0f, 2f);
                if (name.Contains("Stiffness") || name.Contains("GravityPower")) return new AcceptableValueRange<float>(0f, 20f);
                if (name.Contains("Delay")) return new AcceptableValueRange<float>(0f, 600f);
                if (name.Contains("Scale") || name.Contains("Height") || name.Contains("Radius") || name.Contains("Depth"))
                    return new AcceptableValueRange<float>(0f, 20f);
                return new AcceptableValueRange<float>(0f, 100f);
            }
            if (type == typeof(int))
            {
                if (name.Contains("ThresholdMs")) return new AcceptableValueRange<int>(0, 10000);
                if (name.Contains("TimeWindowMs")) return new AcceptableValueRange<int>(0, 60000);
                if (name.Contains("CallThreshold")) return new AcceptableValueRange<int>(1, 100);
            }
            return null;
        }

        private static string SectionKey(string section, string key) => section + "::" + key;
    }
}
