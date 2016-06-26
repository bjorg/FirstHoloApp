﻿using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace FirstHoloApp.Common {

    internal static class DirectXHelper {

        /// <summary>
        /// Function that reads from a binary file asynchronously.
        /// </summary>
        internal static async Task<byte[]> ReadDataAsync(StorageFile file) {
            using(var stream = await file.OpenStreamForReadAsync()) {
                var buffer = new byte[stream.Length];
                await stream.ReadAsync(buffer, 0, buffer.Length);
                return buffer;
            }
        }

        private const float DIPS_PER_INCH = 96.0f;

        /// <summary>
        /// Converts a length in device-independent pixels (DIPs) to a length in physical pixels.
        /// </summary>
        internal static double ConvertDipsToPixels(double dips, double dpi) {
            return Math.Floor(dips * dpi / DIPS_PER_INCH + 0.5f); // Round to nearest integer.
        }

#if DEBUG
        /// <summary>
        /// Check for SDK Layer support.
        /// </summary>
        internal static bool SdkLayersAvailable() {
            try {
                using(var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Null, SharpDX.Direct3D11.DeviceCreationFlags.Debug)) {
                    return true;
                }
            } catch {
                return false;
            }
        }
#endif
    }
}
