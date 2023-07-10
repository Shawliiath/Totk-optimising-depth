namespace Ryujinx.Graphics.Gpu
{
 
    public static class GraphicsConfig
    {
 
        public static float ResScale = 1f;

 
        public static float MaxAnisotropy = -1;

 
        public static string ShadersDumpPath;

 
        /// This can avoid lower resolution on some game when GPU performance is poor
 
        public static bool FastGpuTime = true;
        
        /// Enables or disable fast 2d engine texture copies entirely on cpu when possible
 
        public static bool Fast2DCopy = true;

 
        public static bool EnableMacroJit = true;

 
        public static bool EnableMacroHLE = true;

 
        public static string TitleId;

 bleShaderCache;

        public static bool EnableSpirvCompilationOnVulkan = true;

        public static bool EnableTextureRecompression = false;
    }
}
