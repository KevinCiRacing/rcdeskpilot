using System;
using System.Data;
using System.Globalization;
using System.IO;

namespace Bonsai.Graphics
{
    /// <summary>
    /// Key/value settings persistence, format-compatible with the legacy
    /// Bonsai.Utils.Settings "Application.KeyValues" DataSet table in
    /// frameworkconfig.xml (shared with the Input.Joystick table), so
    /// existing installs keep their options.
    /// </summary>
    public sealed class GameSettings
    {
        private readonly DataSet dataSet = new DataSet("FrameWorkSettings");
        private readonly string path;

        public event Action<string, string> Changed;

        public GameSettings(string filePath)
        {
            path = filePath;
            if (File.Exists(path))
                dataSet.ReadXml(path);
            EnsureTable(dataSet);
        }

        private static DataTable EnsureTable(DataSet set)
        {
            DataTable table = set.Tables["Application.KeyValues"];
            if (table == null)
            {
                table = set.Tables.Add("Application.KeyValues");
                table.Columns.Add("Key", typeof(string));
                table.Columns.Add("Value", typeof(string));
            }
            if (table.PrimaryKey.Length == 0)
                table.PrimaryKey = new[] { table.Columns["Key"] };
            return table;
        }

        public string GetValue(string key)
        {
            DataRow row = dataSet.Tables["Application.KeyValues"].Rows.Find(key);
            return row != null ? row["Value"].ToString() : null;
        }

        public string GetValue(string key, string defaultValue)
        {
            string value = GetValue(key);
            if (value == null)
            {
                SetValue(key, defaultValue);
                return defaultValue;
            }
            return value;
        }

        public int GetInt(string key, int defaultValue)
        {
            int result;
            return int.TryParse(GetValue(key, defaultValue.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : defaultValue;
        }

        public float GetFloat(string key, float defaultValue)
        {
            float result;
            return float.TryParse(GetValue(key, defaultValue.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : defaultValue;
        }

        public bool GetBool(string key, bool defaultValue)
        {
            string value = GetValue(key, defaultValue ? "true" : "false");
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public void SetValue(string key, string value)
        {
            DataTable table = dataSet.Tables["Application.KeyValues"];
            DataRow row = table.Rows.Find(key);
            if (row != null)
            {
                if (row["Value"].ToString() == value)
                    return;
                row["Value"] = value;
            }
            else
            {
                table.Rows.Add(key, value);
            }
            Save();
            var handler = Changed;
            if (handler != null)
                handler(key, value);
        }

        public void SetInt(string key, int value) { SetValue(key, value.ToString(CultureInfo.InvariantCulture)); }
        public void SetFloat(string key, float value) { SetValue(key, value.ToString(CultureInfo.InvariantCulture)); }
        public void SetBool(string key, bool value) { SetValue(key, value ? "true" : "false"); }

        /// <summary>Writes the file, preserving tables other writers (e.g.
        /// InputSettings) may have updated since we loaded.</summary>
        private void Save()
        {
            var merged = new DataSet("FrameWorkSettings");
            if (File.Exists(path))
            {
                try { merged.ReadXml(path); }
                catch { /* corrupt file: rewrite from scratch */ }
            }
            if (merged.Tables.Contains("Application.KeyValues"))
                merged.Tables.Remove("Application.KeyValues");
            DataTable copy = dataSet.Tables["Application.KeyValues"].Copy();
            merged.Tables.Add(copy);
            merged.WriteXml(path);
        }
    }
}
