# Barracuda Style Transfer code sample.

![Style transfer applyed to Book of the Dead Environement](https://github.com/Unity-Technologies/barracuda-release/raw/release/1.1.1/Documentation~/images/BarracudaLanding.png)
Style transfer applyed to [Book of the Dead Environement](https://assetstore.unity.com/packages/essentials/tutorial-projects/book-of-the-dead-environment-121175) via 
[Unity Barracuda](https://github.com/Unity-Technologies/barracuda-release).

This repo is the companion code for the [Style Transfer blog post](http://linkToDo) and presents how to setup the style transfer in a sample scene.

# Instructions:
- Open BarracudaStyleTransfer/SampleScene/SampleScene.unity (tested with Unity 2019.4.1f1, should work with Unity versions from 2018.4 LTS)
- Run the scene. It can take some time to start due to the loading of the network.
- The style transfer script is found on the Style Transfer Camera object

# Controls:
- Left click to enable/disable style transfer.
- Right click to cycle through the styles.
- Mouse wheel up/down to increase/decrease the amount of framerate upsampling (see notes below)

# Style transfer script settings:
- Style Transfer / Model to use:
  - **"Reference"**: Costly and heavier stylization network
  - **"Ref but 32 channels"**: Optimized, lighter stylization network
  
- Framerate Upsampling: 
  *Image-space bidirectional temporal scene reprojection (http://hhoppe.com/proj/bireproj/).*
  - **Use Framerate Upsampling** : enable or disable framerate upsampling
  - **Framerate Upsample Factor** : by how much to (theoretically) multiply the framerate. Also corresponds to how many frames the style transfer computation will be spread on.

Known bugs/limitations:
- When profiler is opened the console will be spammed with 'Profiler tag mismatched' errors. This will be fixed in Barracuda 1.2.0
- The network was trained using sRGB data, the code in this repo explicitely handles conversion from texture to sRGB tensor. Support will be added to Barracuda in a later version.
