using System;
using System.Data;
using System.IO;

namespace Bonsai.Graphics.Input
{
    /// <summary>Joystick axis identifiers. Values MUST keep the legacy
    /// enum order - they are persisted as integers in frameworkconfig.xml.</summary>
    public enum JoystickAxis
    {
        X, Y, Z, Rx, Ry, Rz, Slider1, Slider2, POV1, POV2, POV3, POV4, None
    }

    /// <summary>
    /// Channel-mapping/calibration persistence, format-compatible with the
    /// legacy Bonsai.Utils.Settings "Input.Joystick" DataSet table in
    /// frameworkconfig.xml, so existing transmitter setups keep working.
    /// </summary>
    public sealed class InputSettings
    {
        private readonly DataSet dataSet = new DataSet("Settings");
        private readonly string path;

        public InputSettings(string filePath)
        {
            path = filePath;
            var table = dataSet.Tables.Add("Input.Joystick");
            table.Columns.Add("Function", typeof(string));
            table.Columns.Add("Axis", typeof(int));
            table.Columns.Add("Inverted", typeof(bool));
            table.PrimaryKey = new[] { table.Columns["Function"] };

            if (File.Exists(path))
            {
                var loaded = new DataSet();
                loaded.ReadXml(path);
                if (loaded.Tables.Contains("Input.Joystick"))
                {
                    foreach (DataRow row in loaded.Tables["Input.Joystick"].Rows)
                        table.Rows.Add(row["Function"], Convert.ToInt32(row["Axis"]), Convert.ToBoolean(row["Inverted"]));
                }
            }
        }

        public bool AxisExists(string function)
        {
            return dataSet.Tables["Input.Joystick"].Rows.Find(function) != null;
        }

        public JoystickAxis GetAxis(string function, out bool inverted)
        {
            DataRow row = dataSet.Tables["Input.Joystick"].Rows.Find(function);
            if (row != null)
            {
                inverted = Convert.ToBoolean(row["Inverted"]);
                return (JoystickAxis)Convert.ToInt32(row["Axis"]);
            }
            inverted = false;
            return JoystickAxis.None;
        }

        public void SetAxis(string function, JoystickAxis axis, bool inverted)
        {
            DataRow row = dataSet.Tables["Input.Joystick"].Rows.Find(function);
            if (row != null)
            {
                row["Axis"] = (int)axis;
                row["Inverted"] = inverted;
            }
            else
            {
                dataSet.Tables["Input.Joystick"].Rows.Add(function, (int)axis, inverted);
            }
            Save();
        }

        public void SetDefaultAxis(string function, JoystickAxis axis, bool inverted)
        {
            if (!AxisExists(function))
                SetAxis(function, axis, inverted);
        }

        private void Save()
        {
            dataSet.WriteXml(path);
        }
    }
}
