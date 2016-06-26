using Windows.Graphics.Holographic;
using Windows.Foundation;
using Windows.Perception.Spatial;
using SharpDX.Mathematics.Interop;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX;

namespace FirstHoloApp.Common {

    /// <summary>
    /// Constant buffer used to send hologram position transform to the shader pipeline.
    /// </summary>
    internal struct ViewProjectionConstantBuffer {
        public Matrix4x4 ViewProjectionLeft;
        public Matrix4x4 ViewProjectionRight;
    }

    // Manages DirectX device resources that are specific to a holographic camera, such as the
    // back buffer, ViewProjection constant buffer, and viewport.
    internal class CameraResources : Disposer {

        // Direct3D rendering objects. Required for 3D.
        private RenderTargetView _d3DRenderTargetView;
        private DepthStencilView _d3DDepthStencilView;
        private Texture2D _d3DBackBuffer;

        // Device resource to store view and projection matrices.
        private Buffer _viewProjectionConstantBuffer;

        // Direct3D rendering properties.
        private SharpDX.DXGI.Format _dxgiFormat;
        private RawViewportF _d3DViewport;
        private Size _d3DRenderTargetSize;

        // Indicates whether the camera supports stereoscopic rendering.

        // Indicates whether this camera has a pending frame.
        private bool _framePending;

        // Pointer to the holographic camera these resources are for.
        private readonly HolographicCamera _holographicCamera;

        public CameraResources(HolographicCamera holographicCamera) {
            this._holographicCamera = holographicCamera;
            IsRenderingStereoscopic = holographicCamera.IsStereo;
            _d3DRenderTargetSize = holographicCamera.RenderTargetSize;

            _d3DViewport.Height = (float)_d3DRenderTargetSize.Height;
            _d3DViewport.Width = (float)_d3DRenderTargetSize.Width;
            _d3DViewport.X = 0;
            _d3DViewport.Y = 0;
            _d3DViewport.MinDepth = 0;
            _d3DViewport.MaxDepth = 1;
        }

        /// <summary>
        /// Updates resources associated with a holographic camera's swap chain.
        /// The app does not access the swap chain directly, but it does create
        /// resource views for the back buffer.
        /// </summary>
        public void CreateResourcesForBackBuffer(
            DeviceResources deviceResources,
            HolographicCameraRenderingParameters cameraParameters
        ) {
            var device = deviceResources.D3DDevice;

            // Get the WinRT object representing the holographic camera's back buffer.
            var surface = cameraParameters.Direct3D11BackBuffer;

            // Get a DXGI interface for the holographic camera's back buffer.
            // Holographic cameras do not provide the DXGI swap chain, which is owned
            // by the system. The Direct3D back buffer resource is provided using WinRT
            // interop APIs.
            var surfaceDxgiInterfaceAccess = surface as InteropStatics.IDirect3DDxgiInterfaceAccess;
            var pResource = surfaceDxgiInterfaceAccess.GetInterface(InteropStatics.Id3D11Resource);
            var resource = CppObject.FromPointer<Resource>(pResource);
            Marshal.Release(pResource);

            // Get a Direct3D interface for the holographic camera's back buffer.
            var cameraBackBuffer = resource.QueryInterface<Texture2D>();

            // Determine if the back buffer has changed. If so, ensure that the render target view
            // is for the current back buffer.
            if((null == _d3DBackBuffer) || (_d3DBackBuffer.NativePointer != cameraBackBuffer.NativePointer)) {
                // This can change every frame as the system moves to the next buffer in the
                // swap chain. This mode of operation will occur when certain rendering modes
                // are activated.
                _d3DBackBuffer = cameraBackBuffer;

                // Create a render target view of the back buffer.
                // Creating this resource is inexpensive, and is better than keeping track of
                // the back buffers in order to pre-allocate render target views for each one.
                _d3DRenderTargetView = this.ToDispose(new RenderTargetView(device, BackBufferTexture2D));

                // Get the DXGI format for the back buffer.
                // This information can be accessed by the app using CameraResources::GetBackBufferDXGIFormat().
                var backBufferDesc = BackBufferTexture2D.Description;
                _dxgiFormat = backBufferDesc.Format;

                // Check for render target size changes.
                var currentSize = _holographicCamera.RenderTargetSize;
                if(_d3DRenderTargetSize != currentSize) {
                    // Set render target size.
                    _d3DRenderTargetSize = HolographicCamera.RenderTargetSize;

                    // A new depth stencil view is also needed.
                    this.RemoveAndDispose(ref _d3DDepthStencilView);
                }
            }

            // Refresh depth stencil resources, if needed.
            if(null == DepthStencilView) {
                // Create a depth stencil view for use with 3D rendering if needed.
                var depthStencilDesc = new Texture2DDescription {
                    Format = SharpDX.DXGI.Format.D16_UNorm,
                    Width = (int)RenderTargetSize.Width,
                    Height = (int)RenderTargetSize.Height,
                    ArraySize = IsRenderingStereoscopic ? 2 : 1, // Create two textures when rendering in stereo.
                    MipLevels = 1, // Use a single mipmap level.
                    BindFlags = BindFlags.DepthStencil,
                    SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0)
                };

                using(var depthStencil = new Texture2D(device, depthStencilDesc)) {
                    var depthStencilViewDesc = new DepthStencilViewDescription {
                        Dimension = IsRenderingStereoscopic ? DepthStencilViewDimension.Texture2DArray : DepthStencilViewDimension.Texture2D,
                        Texture2DArray = {
                            ArraySize = IsRenderingStereoscopic ? 2 : 0
                        }
                    };
                    _d3DDepthStencilView = this.ToDispose(new DepthStencilView(device, depthStencil, depthStencilViewDesc));
                }
            }

            // Create the constant buffer, if needed.
            if(null == _viewProjectionConstantBuffer) {

                // Create a constant buffer to store view and projection matrices for the camera.
                var viewProjectionConstantBufferData = new ViewProjectionConstantBuffer();
                _viewProjectionConstantBuffer = this.ToDispose(Buffer.Create(
                    device,
                    BindFlags.ConstantBuffer,
                    ref viewProjectionConstantBufferData));
            }
        }

        /// <summary>
        /// Releases resources associated with a holographic display back buffer.
        /// </summary>
        public void ReleaseResourcesForBackBuffer(DeviceResources deviceResources) {
            var context = deviceResources.D3DDeviceContext;

            this.RemoveAndDispose(ref _d3DBackBuffer);
            this.RemoveAndDispose(ref _d3DRenderTargetView);
            this.RemoveAndDispose(ref _d3DDepthStencilView);

            const int d3D11SimultaneousRenderTargetCount = 8;
            var nullViews = new RenderTargetView[d3D11SimultaneousRenderTargetCount];

            // Ensure system references to the back buffer are released by clearing the render
            // target from the graphics pipeline state, and then flushing the Direct3D context.
            context.OutputMerger.SetRenderTargets(null, nullViews);
            context.Flush();
        }

        public void ReleaseAllDeviceResources(DeviceResources deviceResources) {
            ReleaseResourcesForBackBuffer(deviceResources);
            this.RemoveAndDispose(ref _viewProjectionConstantBuffer);
        }

        /// <summary>
        /// Updates the constant buffer for the display with view and projection
        /// matrices for the current frame.
        /// </summary>
        public void UpdateViewProjectionBuffer(
            DeviceResources deviceResources,
            HolographicCameraPose cameraPose,
            SpatialCoordinateSystem coordinateSystem
        ) {
            // The system changes the viewport on a per-frame basis for system optimizations.
            _d3DViewport.X = (float)cameraPose.Viewport.Left;
            _d3DViewport.Y = (float)cameraPose.Viewport.Top;
            _d3DViewport.Width = (float)cameraPose.Viewport.Width;
            _d3DViewport.Height = (float)cameraPose.Viewport.Height;
            _d3DViewport.MinDepth = 0;
            _d3DViewport.MaxDepth = 1;

            // The projection transform for each frame is provided by the HolographicCameraPose.
            var cameraProjectionTransform = cameraPose.ProjectionTransform;

            // Get a container object with the view and projection matrices for the given
            // pose in the given coordinate system.
            var viewTransformContainer = cameraPose.TryGetViewTransform(coordinateSystem);

            // If TryGetViewTransform returns null, that means the pose and coordinate system
            // cannot be understood relative to one another; content cannot be rendered in this
            // coordinate system for the duration of the current frame.
            // This usually means that positional tracking is not active for the current frame, in
            // which case it is possible to use a SpatialLocatorAttachedFrameOfReference to render
            // content that is not world-locked instead.
            var viewProjectionConstantBufferData = new ViewProjectionConstantBuffer();
            var viewTransformAcquired = viewTransformContainer.HasValue;
            if(viewTransformAcquired) {

                // Otherwise, the set of view transforms can be retrieved.
                var viewCoordinateSystemTransform = viewTransformContainer.Value;

                // Update the view matrices. Holographic cameras (such as Microsoft HoloLens) are
                // constantly moving relative to the world. The view matrices need to be updated
                // every frame.
                viewProjectionConstantBufferData.ViewProjectionLeft = Matrix4x4.Transpose(
                    viewCoordinateSystemTransform.Left * cameraProjectionTransform.Left
                    );
                viewProjectionConstantBufferData.ViewProjectionRight = Matrix4x4.Transpose(
                    viewCoordinateSystemTransform.Right * cameraProjectionTransform.Right
                    );
            }

            // Use the D3D device context to update Direct3D device-based resources.
            var context = deviceResources.D3DDeviceContext;

            // Loading is asynchronous. Resources must be created before they can be updated.
            if(context == null || _viewProjectionConstantBuffer == null || !viewTransformAcquired) {
                _framePending = false;
            } else {
                // Update the view and projection matrices.
                context.UpdateSubresource(ref viewProjectionConstantBufferData, _viewProjectionConstantBuffer);

                _framePending = true;
            }
        }

        /// <summary>
        /// Gets the view-projection constant buffer for the display, and attaches it
        /// to the shader pipeline.
        /// </summary>
        public bool AttachViewProjectionBuffer(DeviceResources deviceResources) {
            // This method uses Direct3D device-based resources.
            var context = deviceResources.D3DDeviceContext;

            // Loading is asynchronous. Resources must be created before they can be updated.
            // Cameras can also be added asynchronously, in which case they must be initialized
            // before they can be used.
            if(context == null || _viewProjectionConstantBuffer == null || !_framePending) {
                return false;
            }

            // Set the viewport for this camera.
            context.Rasterizer.SetViewport(Viewport);

            // Send the constant buffer to the vertex shader.
            context.VertexShader.SetConstantBuffers(1, _viewProjectionConstantBuffer);

            // The template includes a pass-through geometry shader that is used by
            // default on systems that don't support the D3D11_FEATURE_D3D11_OPTIONS3::
            // VPAndRTArrayIndexFromAnyShaderFeedingRasterizer extension. The shader
            // will be enabled at run-time on systems that require it.
            // If your app will also use the geometry shader for other tasks and those
            // tasks require the view/projection matrix, uncomment the following line
            // of code to send the constant buffer to the geometry shader as well.
            //context.GeometryShader.SetConstantBuffers(1, _viewProjectionConstantBuffer);

            _framePending = false;

            return true;
        }

        // Direct3D device resources.
        public RenderTargetView BackBufferRenderTargetView => _d3DRenderTargetView;
        public DepthStencilView DepthStencilView => _d3DDepthStencilView;
        public Texture2D BackBufferTexture2D => _d3DBackBuffer;

        // Render target properties.
        public RawViewportF Viewport => _d3DViewport;
        public SharpDX.DXGI.Format BackBufferDxgiFormat => _dxgiFormat;
        public Size RenderTargetSize => _d3DRenderTargetSize;
        public bool IsRenderingStereoscopic { get; }

        // Associated objects.
        public HolographicCamera HolographicCamera => _holographicCamera;
    }
}
