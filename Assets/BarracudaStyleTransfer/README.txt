REQUIREMENTS:
- Install Burst package through Package Manager window. Without Burst, Unity will show compile errors.
- If already present, uninstall Barracuda pacage through Package Manager (this archive contains a custom Barracuda package)

INSTRUCTIONS:
- Open BarracudaStyleTransfer/SampleScene/SampleScene.unity
- Run the scene. It can take some time to start due to the loading of the network.
- The style transfer script is found on the Style Transfer Camera object

CONTROLS:
- Left click to enable/disable style transfer.
- Right click to cycle through the styles.
- Mouse wheel up/down to increase/decrease the amount of framerate upsampling (see notes below)


STYLE TRANSFER SCRIPT SETTINGS:
Style Transfer Setup:
- Model to use : choose between a more costly and heavier stylization network ("Reference"), and an optimized, lighter stylization network ("Ref but 32 channels")

Framerate Upsampling:
This technique enables higher framerates through the use of Image-space bidirectional scene reprojection (http://hhoppe.com/proj/bireproj/). To do so, it spreads the computing of the style transfer over several frames, and inserts generated intermediate frames in-between each computed style transfer frame using temporal reprojection.
Settings:
- Use Framerate Upsampling : enable or disable framerate upsampling
- Framerate Upsample Factor : by how much to (theoretically) multiply the framerate. Also corresponds to how many frames the style transfer computation will be spread on.
