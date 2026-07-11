using System;
using Microsoft.DirectX;
using Microsoft.DirectX.Direct3D;
using Bonsai.Core.Dialogs;

namespace Bonsai.Core
{
    /// <summary>
    /// Graphics-coupled utility helpers. AWAITING PORT: still written against
    /// Managed DirectX 9 / the DXUT framework; ported or deleted with the
    /// renderer (issues 06+). The renderer-independent helpers live in
    /// Utility.cs.
    /// </summary>
    public partial class Utility
    {
        /// <summary>Returns the view matrix for a cube map face</summary>
        public static Matrix GetCubeMapViewMatrix(CubeMapFace face)
        {
            Vector3 vEyePt = new Vector3(0.0f, 0.0f, 0.0f);
            Vector3 vLookDir = new Vector3();
            Vector3 vUpDir = new Vector3();

            switch (face)
            {
                case CubeMapFace.PositiveX:
                    vLookDir = new Vector3(1.0f, 0.0f, 0.0f);
                    vUpDir = new Vector3(0.0f, 1.0f, 0.0f);
                    break;
                case CubeMapFace.NegativeX:
                    vLookDir = new Vector3(-1.0f, 0.0f, 0.0f);
                    vUpDir = new Vector3(0.0f, 1.0f, 0.0f);
                    break;
                case CubeMapFace.PositiveY:
                    vLookDir = new Vector3(0.0f, 1.0f, 0.0f);
                    vUpDir = new Vector3(0.0f, 0.0f, -1.0f);
                    break;
                case CubeMapFace.NegativeY:
                    vLookDir = new Vector3(0.0f, -1.0f, 0.0f);
                    vUpDir = new Vector3(0.0f, 0.0f, 1.0f);
                    break;
                case CubeMapFace.PositiveZ:
                    vLookDir = new Vector3(0.0f, 0.0f, 1.0f);
                    vUpDir = new Vector3(0.0f, 1.0f, 0.0f);
                    break;
                case CubeMapFace.NegativeZ:
                    vLookDir = new Vector3(0.0f, 0.0f, -1.0f);
                    vUpDir = new Vector3(0.0f, 1.0f, 0.0f);
                    break;
            }

            // Set the view transform for this cubemap surface
            Matrix matView = Matrix.LookAtLH(vEyePt, vLookDir, vUpDir);
            return matView;
        }
        /// <summary>Returns the view matrix for a cube map face</summary>
        public static Matrix GetCubeMapViewMatrix(int face) { return GetCubeMapViewMatrix((CubeMapFace)face); }

        private static bool firstTime = true;
        /// <summary>
        /// Displays the switching to ref device warning, and allows user to quit if they don't want to
        /// </summary>
        public static void DisplaySwitchingToRefWarning(Framework framework, string title)
        {
            DisplaySwitchingToRefWarning(framework, title, null);
        }

        /// <summary>
        /// Displays the switching to ref device warning, and allows user to quit if they don't want to
        /// </summary>
        public static void DisplaySwitchingToRefWarning(Framework framework, string title, string text)
        {
            if (framework.IsShowingMsgBoxOnError)
            {
                // Read the registry key to see if the warning should be skipped
                int skipWarning = 0;
                try
                {
                    using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SwitchRefDialog.KeyLocation))
                    {
                        skipWarning = (int)key.GetValue(SwitchRefDialog.KeyValueName, (int)0);
                    }
                }
                catch { } // Ignore any errors
                if ((skipWarning == 0) && (firstTime)) // Show dialog
                {
                    firstTime = false;
                    if (text != null)
                    {
                        using (SwitchRefDialog dialog = new SwitchRefDialog(title, text))
                        {
                            System.Windows.Forms.Application.Run(dialog);
                            if (dialog.DialogResult == System.Windows.Forms.DialogResult.Cancel)
                            {
                                // Shutdown the application
                                System.Windows.Forms.MessageBox.Show("Closing due to user request");
                                framework.Dispose();
                            }
                        }
                    }
                    else
                    {
                        using (SwitchRefDialog dialog = new SwitchRefDialog(title))
                        {
                            System.Windows.Forms.Application.Run(dialog);
                            if (dialog.DialogResult == System.Windows.Forms.DialogResult.Cancel)
                            {
                                // Shutdown the application
                                System.Windows.Forms.MessageBox.Show("Closing due to user request");
                                framework.Dispose();
                            }
                        }
                    }
                }
            }
        }
    }
}
