using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(Renderer))]
    public class PSXObjectExporter : MonoBehaviour
    {
        public PSXBPP BitDepth = PSXBPP.TEX_8BIT; // Defines the bit depth of the texture (e.g., 4BPP, 8BPP)

        public PSXTexture2D Texture { get; set; } // Stores the converted PlayStation-style texture
        public PSXMesh Mesh { get; set; } // Stores the converted PlayStation-style mesh

        /// <summary>
        /// Converts the object's material texture into a PlayStation-compatible texture.
        /// </summary>
        public void CreatePSXTexture2D()
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer.sharedMaterial != null && renderer.sharedMaterial.mainTexture is Texture2D texture)
            {
                if (Texture == null || Texture.NeedUpdate(BitDepth, texture))
                {
                    Texture = PSXTexture2D.CreateFromTexture2D(texture, BitDepth);
                    Texture.OriginalTexture = texture; // Stores reference to the original texture
                }
            }
            else
            {
                //TODO: Better handle object with default texture
                Texture = new PSXTexture2D()
                {
                    BitDepth = BitDepth,
                    Width = 0,
                    Height = 0,
                };
                Texture.OriginalTexture = null;
            }
        }

        /// <summary>
        /// Converts the object's mesh into a PlayStation-compatible mesh.
        /// </summary>
        public void CreatePSXMesh()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (gameObject.isStatic)
            {
                // Static meshes take object transformation into account
                Mesh = PSXMesh.CreateFromUnityMesh(meshFilter.sharedMesh, Texture.Width, Texture.Height, transform);
            }
            else
            {
                // Dynamic meshes do not consider object transformation
                Mesh = PSXMesh.CreateFromUnityMesh(meshFilter.sharedMesh, Texture.Width, Texture.Height);
            }
        }
    }
}
