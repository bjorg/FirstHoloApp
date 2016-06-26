using System.Numerics;

namespace FirstHoloApp.Content {

    /// <summary>
    /// Constant buffer used to send hologram position transform to the shader pipeline.
    /// </summary>
    internal struct ModelConstantBuffer {
        public Matrix4x4 Model;
    }

    /// <summary>
    /// Used to send per-vertex data to the vertex shader.
    /// </summary>
    internal struct VertexPositionColor {
        public VertexPositionColor(Vector3 pos, Vector3 color) {
            this.Pos = pos;
            this.Color = color;
        }

        public Vector3 Pos;
        public Vector3 Color;
    };
}
