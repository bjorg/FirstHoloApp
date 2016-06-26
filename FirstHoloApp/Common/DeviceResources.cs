using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Holographic;

namespace FirstHoloApp.Common {

    /// <summary>
    /// Controls all the DirectX device resources.
    /// </summary>
    internal class DeviceResources : Disposer {

        // Notifies the application that owns DeviceResources when the Direct3D device is lost.
        public event EventHandler DeviceLost;

        // Notifies the application that owns DeviceResources when the Direct3D device is restored.
        public event EventHandler DeviceRestored;

        // Direct3D objects.
        private Device3 _d3DDevice;
        private DeviceContext3 _d3DContext;
        private SharpDX.DXGI.Adapter3 _dxgiAdapter;

        // Direct3D interop objects.
        private IDirect3DDevice _d3DInteropDevice;

        // Direct2D factories.
        private SharpDX.Direct2D1.Factory2 _d2DFactory;
        private SharpDX.DirectWrite.Factory1 _dwriteFactory;
        private SharpDX.WIC.ImagingFactory2 _wicFactory;

        // The holographic space provides a preferred DXGI adapter ID.
        private HolographicSpace _holographicSpace;

        // Properties of the Direct3D device currently in use.
        private FeatureLevel _d3DFeatureLevel = FeatureLevel.Level_10_0;

        // Whether or not the current Direct3D device supports the optional feature
        // for setting the render target array index from the vertex shader stage.
        private bool _d3DDeviceSupportsVprt;

        // Back buffer resources, etc. for attached holographic cameras.
        private readonly Dictionary<uint, CameraResources> _cameraResourcesDictionary = new Dictionary<uint, CameraResources>();
        private readonly object cameraResourcesLock = new object();

        /// <summary>
        /// Constructor for DeviceResources.
        /// </summary>
        public DeviceResources() {
            CreateDeviceIndependentResources();
        }

        /// <summary>
        /// Configures resources that don't depend on the Direct3D device.
        /// </summary>
        private void CreateDeviceIndependentResources() {
            // Dispose previous references and set to null
            this.RemoveAndDispose(ref _d2DFactory);
            this.RemoveAndDispose(ref _dwriteFactory);
            this.RemoveAndDispose(ref _wicFactory);

            // Initialize Direct2D resources.
            var debugLevel = SharpDX.Direct2D1.DebugLevel.None;
#if DEBUG
            if(DirectXHelper.SdkLayersAvailable()) {
                debugLevel = SharpDX.Direct2D1.DebugLevel.Information;
            }
#endif

            // Initialize the Direct2D Factory.
            _d2DFactory = this.ToDispose(
                new SharpDX.Direct2D1.Factory2(
                    SharpDX.Direct2D1.FactoryType.SingleThreaded,
                    debugLevel
                    )
                );

            // Initialize the DirectWrite Factory.
            _dwriteFactory = this.ToDispose(
                new SharpDX.DirectWrite.Factory1(SharpDX.DirectWrite.FactoryType.Shared)
                );

            // Initialize the Windows Imaging Component (WIC) Factory.
            _wicFactory = this.ToDispose(
                new SharpDX.WIC.ImagingFactory2()
                );
        }

        public void SetHolographicSpace(HolographicSpace holographicSpace) {
            // Cache the holographic space. Used to re-initalize during device-lost scenarios.
            this._holographicSpace = holographicSpace;

            InitializeUsingHolographicSpace();
        }

        public void InitializeUsingHolographicSpace() {

            // The holographic space might need to determine which adapter supports
            // holograms, in which case it will specify a non-zero PrimaryAdapterId.
            var shiftPos = sizeof(uint);
            var id = _holographicSpace.PrimaryAdapterId.LowPart | (((ulong)_holographicSpace.PrimaryAdapterId.HighPart) << shiftPos);

            // When a primary adapter ID is given to the app, the app should find
            // the corresponding DXGI adapter and use it to create Direct3D devices
            // and device contexts. Otherwise, there is no restriction on the DXGI
            // adapter the app can use.
            if(id != 0) {
                // Create the DXGI factory.
                using(var dxgiFactory4 = new SharpDX.DXGI.Factory4()) {
                    // Retrieve the adapter specified by the holographic space.
                    IntPtr adapterPtr;
                    dxgiFactory4.EnumAdapterByLuid((long)id, InteropStatics.IdxgiAdapter3, out adapterPtr);

                    if(adapterPtr != IntPtr.Zero) {
                        _dxgiAdapter = new SharpDX.DXGI.Adapter3(adapterPtr);
                    }
                }
            } else {
                this.RemoveAndDispose(ref _dxgiAdapter);
            }

            CreateDeviceResources();

            _holographicSpace.SetDirect3D11Device(_d3DInteropDevice);
        }

        /// <summary>
        /// Configures the Direct3D device, and stores handles to it and the device context.
        /// </summary>
        private void CreateDeviceResources() {
            DisposeDeviceAndContext();

            // This flag adds support for surfaces with a different Color channel ordering
            // than the API default. It is required for compatibility with Direct2D.
            var creationFlags = DeviceCreationFlags.BgraSupport;

#if DEBUG
            if(DirectXHelper.SdkLayersAvailable()) {
                // If the project is in a debug build, enable debugging via SDK Layers with this flag.
                creationFlags |= DeviceCreationFlags.Debug;
            }
#endif

            // This array defines the set of DirectX hardware feature levels this app will support.
            // Note the ordering should be preserved.
            // Note that HoloLens supports feature level 11.1. The HoloLens emulator is also capable
            // of running on graphics cards starting with feature level 10.0.
            FeatureLevel[] featureLevels =
            {
                FeatureLevel.Level_12_1,
                FeatureLevel.Level_12_0,
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            // Create the Direct3D 11 API device object and a corresponding context.
            try {
                if(null != _dxgiAdapter) {
                    using(var device = new Device(_dxgiAdapter, creationFlags, featureLevels)) {
                        // Store pointers to the Direct3D 11.1 API device.
                        _d3DDevice = this.ToDispose(device.QueryInterface<Device3>());
                    }
                } else {
                    using(var device = new Device(DriverType.Hardware, creationFlags, featureLevels)) {
                        // Store a pointer to the Direct3D device.
                        _d3DDevice = this.ToDispose(device.QueryInterface<Device3>());
                    }
                }
            } catch {
                // If the initialization fails, fall back to the WARP device.
                // For more information on WARP, see:
                // http://go.microsoft.com/fwlink/?LinkId=286690
                using(var device = new Device(DriverType.Warp, creationFlags, featureLevels)) {
                    _d3DDevice = this.ToDispose(device.QueryInterface<Device3>());
                }
            }

            // Cache the feature level of the device that was created.
            _d3DFeatureLevel = _d3DDevice.FeatureLevel;

            // Store a pointer to the Direct3D immediate context.
            _d3DContext = this.ToDispose(_d3DDevice.ImmediateContext3);

            // Acquire the DXGI interface for the Direct3D device.
            using(var dxgiDevice = _d3DDevice.QueryInterface<SharpDX.DXGI.Device3>()) {
                // Wrap the native device using a WinRT interop object.
                IntPtr pUnknown;
                var hr = InteropStatics.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out pUnknown);
                if(hr == 0) {
                    _d3DInteropDevice = (IDirect3DDevice)Marshal.GetObjectForIUnknown(pUnknown);
                    Marshal.Release(pUnknown);
                }

                // Store a pointer to the DXGI adapter.
                // This is for the case of no preferred DXGI adapter, or fallback to WARP.
                _dxgiAdapter = this.ToDispose(dxgiDevice.Adapter.QueryInterface<SharpDX.DXGI.Adapter3>());
            }

            // Check for device support for the optional feature that allows setting the render target array index from the vertex shader stage.
            var options = _d3DDevice.CheckD3D113Features3();
            if(options.VPAndRTArrayIndexFromAnyShaderFeedingRasterizer) {
                _d3DDeviceSupportsVprt = true;
            }
        }

        /// <summary>
        /// Disposes of a device-based resources.
        /// </summary>
        private void DisposeDeviceAndContext() {
            // Dispose existing references to Direct3D 11 device and contxt, and set to null.
            this.RemoveAndDispose(ref _d3DDevice);
            this.RemoveAndDispose(ref _d3DContext);
            this.RemoveAndDispose(ref _dxgiAdapter);

            // Release the interop device.
            _d3DInteropDevice = null;
        }

        /// <summary>
        /// Validates the back buffer for each HolographicCamera and recreates
        /// resources for back buffers that have changed.
        /// Locks the set of holographic camera resources until the function exits.
        /// </summary>
        public void EnsureCameraResources(HolographicFrame frame, HolographicFramePrediction prediction) {
            UseHolographicCameraResources(cameraResourcesDictionary => {
                foreach(var pose in prediction.CameraPoses) {
                    var renderingParameters = frame.GetRenderingParameters(pose);
                    var cameraResources = cameraResourcesDictionary[pose.HolographicCamera.Id];

                    cameraResources.CreateResourcesForBackBuffer(this, renderingParameters);
                }
            });
        }

        /// <summary>
        /// Prepares to allocate resources and adds resource views for a camera.
        /// Locks the set of holographic camera resources until the function exits.
        /// </summary>
        public void AddHolographicCamera(HolographicCamera camera) {
            UseHolographicCameraResources(cameraResourcesDictionary => {
                cameraResourcesDictionary.Add(camera.Id, new CameraResources(camera));
            });
        }

        // Deallocates resources for a camera and removes the camera from the set.
        // Locks the set of holographic camera resources until the function exits.
        public void RemoveHolographicCamera(HolographicCamera camera) {
            UseHolographicCameraResources(cameraResourcesDictionary => {
                var cameraResources = cameraResourcesDictionary[camera.Id];

                if(null != cameraResources) {
                    cameraResources.ReleaseResourcesForBackBuffer(this);
                    cameraResourcesDictionary.Remove(camera.Id);
                }
            });
        }

        /// <summary>
        /// Recreate all device resources and set them back to the current state.
        /// Locks the set of holographic camera resources until the function exits.
        /// </summary>
        public void HandleDeviceLost() {
            DeviceLost?.Invoke(this, null);

            UseHolographicCameraResources(cameraResourcesDictionary => {
                foreach(var pair in cameraResourcesDictionary) {
                    var cameraResources = pair.Value;
                    cameraResources.ReleaseAllDeviceResources(this);
                }
            });

            InitializeUsingHolographicSpace();

            DeviceRestored?.Invoke(this, null);
        }

        /// <summary>
        /// Call this method when the app suspends. It provides a hint to the driver that the app
        /// is entering an idle state and that temporary buffers can be reclaimed for use by other apps.
        /// </summary>
        public void Trim() {
            _d3DContext.ClearState();

            using(var dxgiDevice = _d3DDevice.QueryInterface<SharpDX.DXGI.Device3>()) {
                dxgiDevice.Trim();
            }
        }

        /// <summary>
        /// Present the contents of the swap chain to the screen.
        /// Locks the set of holographic camera resources until the function exits.
        /// </summary>
        public void Present(ref HolographicFrame frame) {
            // By default, this API waits for the frame to finish before it returns.
            // Holographic apps should wait for the previous frame to finish before
            // starting work on a new frame. This allows for better results from
            // holographic frame predictions.
            var presentResult = frame.PresentUsingCurrentPrediction(
                HolographicFramePresentWaitBehavior.WaitForFrameToFinish
                );

            var prediction = frame.CurrentPrediction;
            UseHolographicCameraResources(cameraResourcesDictionary => {
                foreach(var cameraPose in prediction.CameraPoses) {

                    // This represents the device-based resources for a HolographicCamera.
                    var cameraResources = cameraResourcesDictionary[cameraPose.HolographicCamera.Id];

                    // Discard the contents of the render target.
                    // This is a valid operation only when the existing contents will be
                    // entirely overwritten. If dirty or scroll rects are used, this call
                    // should be removed.
                    _d3DContext.DiscardView(cameraResources.BackBufferRenderTargetView);

                    // Discard the contents of the depth stencil.
                    _d3DContext.DiscardView(cameraResources.DepthStencilView);
                }
            });

            // The PresentUsingCurrentPrediction API will detect when the graphics device
            // changes or becomes invalid. When this happens, it is considered a Direct3D
            // device lost scenario.
            if(presentResult == HolographicFramePresentResult.DeviceRemoved) {
                // The Direct3D device, context, and resources should be recreated.
                HandleDeviceLost();
            }
        }

        public delegate void SwapChainAction(Dictionary<uint, CameraResources> cameraResourcesDictionary);

        public delegate bool SwapChainActionWithResult(Dictionary<uint, CameraResources> cameraResourcesDictionary);

        /// <summary>
        /// Device-based resources for holographic cameras are stored in a std::map. Access this list by providing a
        /// callback to this function, and the std::map will be guarded from add and remove
        /// events until the callback returns. The callback is processed immediately and must
        /// not contain any nested calls to UseHolographicCameraResources.
        /// The callback takes a parameter of type Dictionary<uint, CameraResources> _cameraResourcesDictionary
        /// through which the list of cameras will be accessed.
        /// The callback also returns a boolean result.
        /// </summary>
        public bool UseHolographicCameraResources(SwapChainActionWithResult callback) {
            bool success;
            lock(cameraResourcesLock) {
                success = callback(_cameraResourcesDictionary);
            }
            return success;
        }

        /// <summary>
        /// Device-based resources for holographic cameras are stored in a std::map. Access this list by providing a
        /// callback to this function, and the std::map will be guarded from add and remove
        /// events until the callback returns. The callback is processed immediately and must
        /// not contain any nested calls to UseHolographicCameraResources.
        /// The callback takes a parameter of type Dictionary<uint, CameraResources> _cameraResourcesDictionary
        /// through which the list of cameras will be accessed.
        /// </summary>
        public void UseHolographicCameraResources(SwapChainAction callback) {
            lock(cameraResourcesLock) {
                callback(_cameraResourcesDictionary);
            }
        }

        #region Property accessors
        public Device3 D3DDevice => _d3DDevice;
        public DeviceContext3 D3DDeviceContext => _d3DContext;
        public SharpDX.DXGI.Adapter3 DxgiAdapter => _dxgiAdapter;
        public IDirect3DDevice D3DInteropDevice => _d3DInteropDevice;
        public SharpDX.Direct2D1.Factory2 D2DFactory => _d2DFactory;
        public SharpDX.DirectWrite.Factory1 DWriteFactory => _dwriteFactory;
        public SharpDX.WIC.ImagingFactory2 WicImagingFactory => _wicFactory;
        public FeatureLevel D3DDeviceFeatureLevel => _d3DFeatureLevel;
        public bool D3DDeviceSupportsVprt => _d3DDeviceSupportsVprt;
        #endregion
    }
}
