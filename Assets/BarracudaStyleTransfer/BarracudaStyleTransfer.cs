using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq; // Concat()
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Barracuda;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine.Profiling;
using UnityEngine.UI;


public class BarracudaStyleTransfer : MonoBehaviour
{
    public enum UsedModel
    {
        Reference = 0,
        RefBut32Channels = 1
    }

    [System.Serializable]
    public class StyleDefinition
    {
        public Texture2D image;
        public int reprojectionHalo;
        public int reprojectionHaloSky;
    }

    [System.Serializable]
    public class InternalStyleTransferSetup
    {
        public WorkerFactory.Type workerType = WorkerFactory.Type.Compute;
        [HideInInspector] public NNModel[] nnModels;
        [HideInInspector] public Vector4 postNetworkColorBias;
        public bool forceBilinearUpsample2DInModel = true;
        public ComputeInfo.ChannelsOrder channelsOrder = ComputeInfo.ChannelsOrder.NCHW;
        public bool shouldSaveStyleTransferDataAsAsset = true;
        public bool shouldUseSRGBTensor = true;
    }

    [Header("Style Transfer Setup")]
    [TextArea(5, 5)]
    public string Notes0 =
        "Model To Use -> \n" +
        "    Reference: Slow model. Intense stylisation (too much?). Unite 2019 model. \n" +
        "    Ref But 32 channels: Fastest, less intense style and good quality (the default).";
    public UsedModel modelToUse = UsedModel.RefBut32Channels;
    public InternalStyleTransferSetup internalSetup;

    [Header("Style Transfer Styles")]
    [TextArea(5, 5)]
    [HideInInspector] public string Notes1 =
        "Style Definition -> \n" +
        "    Image: the style. \n" +
        "    Reprojection Halo: for temporal upsampling. Size in pixels of the halo added around objects by this style. \n" +
        "    Reprojection Halo Sky: same, for halos of objects in front of the skybox (tends to be larger). \n";
    [HideInInspector] public StyleDefinition[] styles;

    //Down and up sampling
    private const int UpsamplingPass = 0;
    private Material upAndDownSamplingMat;
    private int originalDirectionalLightShadowData;

    //Style transfer
    private readonly bool verbose = false;
    private NNModel nnModel => internalSetup.nnModels[(int)modelToUse];
    private Texture2D StyleImage => styles[currentStyleIndex].image;
    private Model model;
    private List<string> layerNameToPatch;
    private IWorker worker;
    private Camera screenCamera;
    private int setWidth;
    private Texture2D setStyle;
    private Tensor input;
    private Tensor styleInput;
    private List<float[]> predictionAlphasBetasData;
    private RenderTexture renderTarget;
    private int frameNumber;
    private bool shouldApplyStyleTransfer = false;

    [Header("Framerate Upsampling")]
    public bool useFramerateUpsampling = true;
    public int frameRateUpsampleFactor = 4;
    public int styleSkyHaloSize => styles[currentStyleIndex].reprojectionHaloSky;
    public int styleHaloSize => styles[currentStyleIndex].reprojectionHalo;
    private int borderHaloSize = 30;
    private ComputeShader frameToTilesConverter;
    private ComputeShader styleDepthMotionCS;
    private ComputeShader bFrameGenerator;
    private Tensor inputTiles;
    private Tensor[] iFrame2InputTiles;
    private RenderTexture iFrame0;
    private RenderTexture iFrame1;
    private RenderTexture iFrame2;
    private RenderTexture bFrame;
    private RenderTexture iFrame0SDMV; // Stylized Depth + Motion Vector
    private RenderTexture iFrame1SDMV; // Stylized Depth + Motion Vector
    private RenderTexture iFrame2SDMV; // Stylized Depth + Motion Vector
    private ComputeShader tensorToTextureSRGB;

    [Header("UI")]
    public bool shouldDisplayInset = true;
    public Vector2 styleInsetBottomLeftOffset = new Vector2(30, 30);
    public int styleInsetSize = 128;
    public int styleInsetBorderWidth = 4;
    private Texture2D styleImageSrgb;
    private Texture2D whiteBorder;

    // Other variables
    private Camera targetCamera;
    private RenderTexture styleOutput;
    private List<Layer> predictionAlphasBetas;
    private int targetCameraCullingMask;
    private UsedModel setModelToUse;
    private bool setUseBidirectional;
    private int setBidirWidth = 0;
    private int setBidirHeight = 0;
    private int currentFrame = 0;
    private int currentStyleIndex = 0;
    private Text fpsUpsampleText;

    // Async Inference Variables
    private bool firstWorker = true;
    private IEnumerator inferenceCoroutine;
    private int inferenceCurrentLayer;
    private float[] modelLayerTimingsPercent = new float[]
    {
        0.0f,
        0.0f,
        0.0f,
        0.0f,
        0.0f,
        0.021941f,
        0.017204f,
        0.036529f,
        0.009227f,
        0.042545f,
        0.009238f,
        0.042679f,
        0.009394f,
        0.011978f,
        0.042437f,
        0.009405f,
        0.042346f,
        0.009383f,
        0.011913f,
        0.042405f,
        0.009378f,
        0.042512f,
        0.009330f,
        0.011924f,
        0.042443f,
        0.009388f,
        0.042427f,
        0.009319f,
        0.011907f,
        0.042427f,
        0.009383f,
        0.042448f,
        0.009405f,
        0.011994f,
        0.011854f,
        0.099505f,
        0.017209f,
        0.024267f,
        0.171338f,
        0.012918f,
    };


    void Start()
    {
        // Initialize watchers
        setModelToUse = modelToUse;
        setUseBidirectional = useFramerateUpsampling;

        // Update framerate upsample factor display
        fpsUpsampleText = GameObject.Find("Framerate Upsample Display").GetComponent<Text>();

        // Load assets from Resources folder
        internalSetup.nnModels = new NNModel[]
        {
            Resources.Load<NNModel>("adele_2"),
            Resources.Load<NNModel>("model_32channels")
        };
        bFrameGenerator = Resources.Load<ComputeShader>("BFrameGenerator");
        frameToTilesConverter = Resources.Load<ComputeShader>("FrameToTilesConverter");
        styleDepthMotionCS = Resources.Load<ComputeShader>("StyleDepthMotion");
        tensorToTextureSRGB = Resources.Load<ComputeShader>("TextureToSRGBTensor");

        // Manually set correct post network bias
        internalSetup.postNetworkColorBias = new Vector4(0.4850196f, 0.4579569f, 0.4076039f, 0.0f);

        //All model should be defined in internalStyleTransferSetup
        Debug.Assert(Enum.GetNames(typeof(UsedModel)).Length == internalSetup.nnModels.Length);
        Debug.Assert(internalSetup.nnModels.All(m => m != null));

        ComputeInfo.channelsOrder = internalSetup.channelsOrder;

        targetCamera = GetComponent<Camera>();
        targetCameraCullingMask = targetCamera.cullingMask;

        //Prepare style transfer prediction and runtime worker at load time (to avoid memory allocation at runtime)
        PrepareStylePrediction();
        CreateBarracudaWorker();

        SetupStyleTransfer();
    }

    void Update()
    {
        // Avoid run-time changing of model to use (not supported)
        if (modelToUse != setModelToUse)
            modelToUse = setModelToUse;

        // Controls: Enable/Disable style transfer
        if (Input.GetMouseButtonDown(0))
        {
            shouldApplyStyleTransfer = !shouldApplyStyleTransfer;
        }

        // Controls: Cycle through the given styles
        if (Input.GetMouseButtonDown(1))
        {
            if (shouldApplyStyleTransfer)
            {
                ++currentStyleIndex;
                currentStyleIndex %= styles.Length;
            }
        }
        // Controls: increase/decrease framerate upsample factor (how many interpolated frames to display in between computed frames)
        if (Input.mouseScrollDelta.y > 0.0 && frameRateUpsampleFactor < 16)
            frameRateUpsampleFactor++;
        else if (Input.mouseScrollDelta.y < 0.0 && frameRateUpsampleFactor > 1)
            frameRateUpsampleFactor--;

        // Update framerate upsample factor display
        if (fpsUpsampleText != null)
            fpsUpsampleText.text = "Mouse wheel:	framerate upsample x" + frameRateUpsampleFactor.ToString();

        // Framerate upsampling time factor hack, for correct motion vectors
        if (useFramerateUpsampling == true)
        {
            if (currentFrame == frameRateUpsampleFactor - 1)
            {
                targetCamera.cullingMask = targetCameraCullingMask;
                Time.timeScale = frameRateUpsampleFactor;
            }
            else
            {
                //targetCamera.cullingMask = 0;
                Time.timeScale = 0.0f;
            }
        }
        else
        {
            Time.timeScale = 1.0f;
        }
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (shouldApplyStyleTransfer == true && StyleImage != setStyle)
            SetupStyleTransfer();
        if (useFramerateUpsampling == true && (targetCamera.pixelWidth != setBidirWidth || targetCamera.pixelHeight != setBidirHeight || useFramerateUpsampling != setUseBidirectional))
            SetupBidirectionalReprojection();

        if (useFramerateUpsampling == false || Time.frameCount < 10)
            OnRenderImageNormal(source, destination);
        else
            OnRenderImageTemporal(source, destination);
    }

    void OnDestroy()
    {
        if (worker != null)
            worker.Dispose();
        if (input != null)
            input.Dispose();
        if (styleInput != null)
            styleInput.Dispose();
        if (styleOutput != null)
            styleOutput.Release();

        // BIDIRECTIONAL ITERATIVE REPROJECTION SETUP
        if (iFrame0 != null)
            iFrame0.Release();
        if (iFrame1 != null)
            iFrame1.Release();
        if (iFrame2 != null)
            iFrame2.Release();
        if (bFrame != null)
            bFrame.Release();
        if (iFrame0SDMV != null)
            iFrame0SDMV.Release();
        if (iFrame1SDMV != null)
            iFrame1SDMV.Release();
        if (iFrame2SDMV != null)
            iFrame2SDMV.Release();

        shouldApplyStyleTransfer = false;
    }


    // RENDER WITHOUT BIDIRECTIONAL REPROJECTION
    private void OnRenderImageNormal(RenderTexture source, RenderTexture destination)
    {
        if (useFramerateUpsampling == false)
        {
            if (shouldApplyStyleTransfer == false)
            {
                Graphics.Blit(source, destination);
                return;
            }
            else
            {
                input = new Tensor(source, 3);
                CustomPinTensorFromTexture(input);
                Dictionary<string, Tensor> temp = new Dictionary<string, Tensor>();
                temp.Add("frame", input);
                worker.Execute(temp);
                Tensor O = worker.PeekOutput();
                RenderTexture tempRT = RenderTexture.GetTemporary(targetCamera.pixelWidth, targetCamera.pixelHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                CustomTensorToRenderTexture(O, tempRT, 0, 0, Vector4.one, internalSetup.postNetworkColorBias);
                if (shouldDisplayInset == true)
                {
                    Graphics.CopyTexture(whiteBorder, 0, 0, 0, 0, whiteBorder.width, whiteBorder.height, tempRT, 0, 0, (int)styleInsetBottomLeftOffset.x - styleInsetBorderWidth, (int)styleInsetBottomLeftOffset.y - styleInsetBorderWidth);
                    Graphics.CopyTexture(styleImageSrgb, 0, 0, 0, 0, styleImageSrgb.width, styleImageSrgb.height, tempRT, 0, 0, (int)styleInsetBottomLeftOffset.x, (int)styleInsetBottomLeftOffset.y);
                }
                Graphics.Blit(tempRT, destination);
                input.Dispose();
                O.Dispose();
                tempRT.Release();
                return;
            }
        }
    }

    // RENDER WITH BIDIRECTIONAL ITERATIVE REPROJECTION
    private void OnRenderImageTemporal(RenderTexture source, RenderTexture destination)
    {
        // SHIFT TIMELINE AT NEW IFRAME
        if (currentFrame == 0)
        {
            RenderTexture temp = iFrame0;
            iFrame0 = iFrame1;
            iFrame1 = iFrame2;
            iFrame2 = temp;

            RenderTexture tempSDMV = iFrame0SDMV;
            iFrame0SDMV = iFrame1SDMV;
            iFrame1SDMV = iFrame2SDMV;
            iFrame2SDMV = tempSDMV;
        }

        // HANDLE IFRAME 2 GENERATION
        {
            if (shouldApplyStyleTransfer == false || (currentFrame != 0 && firstWorker == true))
            {
                Graphics.Blit(source, iFrame2);
            }
            else
            {
                if (currentFrame == 0)
                {
                    if (firstWorker == false)
                    {
                        //Ensure we reach the end of the network also MoveNext need to be called until false for memory to be marked as releasable.
                        while (inferenceCoroutine.MoveNext()) { }
                        Tensor O = worker.PeekOutput();
                        CustomTensorToRenderTexture(O, iFrame1, 0, 0, Vector4.one, internalSetup.postNetworkColorBias);
                        O.Dispose();
                        input.Dispose();
                    }
                    firstWorker = false;

                    input = new Tensor(source, 3);
                    CustomPinTensorFromTexture(input);
                    inferenceCoroutine = worker.StartManualSchedule(input);
                    inferenceCurrentLayer = 0;
                }
                // Execute divided workload for this frame
                float frameWorkload = 1.0f / (float)frameRateUpsampleFactor;
                float frameWork = 0.0f;
                while (frameWork < frameWorkload && inferenceCurrentLayer < model.layers.Count)
                {
                    inferenceCoroutine.MoveNext();
                    frameWork += modelToUse == UsedModel.RefBut32Channels ? modelLayerTimingsPercent[inferenceCurrentLayer] : 1.0f / model.layers.Count;
                    inferenceCurrentLayer++;
                }
            }
        }

        // STORE MOTION VECTORS FOR IFRAME2 + STYLIZE
        if (currentFrame == 0 && frameRateUpsampleFactor > 1)
        {
            styleDepthMotionCS.SetInt("_FrameWidth", setBidirWidth);
            styleDepthMotionCS.SetInt("_FrameHeight", setBidirHeight);
            styleDepthMotionCS.SetInt("_SkyHaloSize", shouldApplyStyleTransfer == true ? styleSkyHaloSize : 0);
            styleDepthMotionCS.SetInt("_HaloSize", shouldApplyStyleTransfer == true ? styleHaloSize : 0);
            styleDepthMotionCS.SetTexture(0, "_StyleDepthMotionTex", iFrame2SDMV);
            styleDepthMotionCS.Dispatch(0, (int)Mathf.Ceil(iFrame2SDMV.width / 8.0f), (int)Mathf.Ceil(iFrame2SDMV.height / 8.0f), 1);
        }

        // DISPLAY CURRENT BFRAME LERPING FROM IFRAME 0 TO IFRAME 1
        {
            if (false && currentFrame == 0)
            {
                Graphics.Blit(iFrame0, bFrame);
            }
            else
            {
                bFrameGenerator.SetTexture(0, "_IFrame0", iFrame0);
                bFrameGenerator.SetTexture(0, "_IFrame1", iFrame1);
                bFrameGenerator.SetTexture(0, "_IFrame0SDMV", iFrame0SDMV);
                bFrameGenerator.SetTexture(0, "_IFrame1SDMV", iFrame1SDMV);
                bFrameGenerator.SetTexture(0, "_BFrame", bFrame);
                bFrameGenerator.SetInt("_FrameWidth", setBidirWidth);
                bFrameGenerator.SetInt("_FrameHeight", setBidirHeight);
                bFrameGenerator.SetInt("_BorderHaloSize", shouldApplyStyleTransfer == true ? borderHaloSize : 0);
                bFrameGenerator.SetVector("_TexelSize", new Vector4(1.0f / (float)setBidirWidth, 1.0f / (float)setBidirHeight, 0.0f, 0.0f));
                bFrameGenerator.SetFloat("_BFrameAlpha", Time.frameCount < 8 ? 0.0f : currentFrame / (float)(frameRateUpsampleFactor));
                bFrameGenerator.SetFloat("_PreviousBFrameAlpha", currentFrame <= 1 ? -1.0f : (currentFrame - 1) / (float)(frameRateUpsampleFactor));
                bFrameGenerator.SetInt("_CurrentBFrame", currentFrame);

                bFrameGenerator.SetBool("_DisplayInset", shouldApplyStyleTransfer && shouldDisplayInset);
                bFrameGenerator.SetTexture(0, "_StyleImageSrgb", styleImageSrgb);
                bFrameGenerator.SetInts("_StyleImageSrgb_TexelSize", styleImageSrgb.width, styleImageSrgb.height);
                bFrameGenerator.SetInts("_StyleInsetBottomLeftOffset", (int)styleInsetBottomLeftOffset.x, (int)styleInsetBottomLeftOffset.y);
                bFrameGenerator.SetInt("_StyleInsetBorderWidth", styleInsetBorderWidth);

                bFrameGenerator.Dispatch(0, (int)Mathf.Ceil(iFrame2SDMV.width / 8.0f), (int)Mathf.Ceil(iFrame2SDMV.height / 8.0f), 1);
            }

            Graphics.Blit(bFrame, destination);
        }

        // UPDATE FRAME COUNT
        currentFrame = (currentFrame + 1) % frameRateUpsampleFactor;
    }
    

    private void SetupStyleTransfer()
    {
        setStyle = StyleImage;

        if (input != null)
            input.Dispose();
        if (styleInput != null)
            styleInput.Dispose();
        if (styleOutput != null)
            styleOutput.Release();

        styleImageSrgb = new Texture2D(StyleImage.width, StyleImage.height, TextureFormat.RGBA32, false, false);
        Color[] original = StyleImage.GetPixels();
        Color[] modifiedColors = new Color[original.Length];
        for (int i = 0; i < original.Length; i++)
        {
            modifiedColors[i] = original[i].linear;
        }
        styleImageSrgb.SetPixels(modifiedColors);
        styleImageSrgb.Apply();
        TextureScaler.Bilinear(styleImageSrgb, styleInsetSize - styleInsetBorderWidth * 2, styleInsetSize - styleInsetBorderWidth * 2);

        ComputeInfo.channelsOrder = ComputeInfo.ChannelsOrder.NCHW;

        PrepareStylePrediction();
        PatchRuntimeWorkerWithStylePrediction();

        whiteBorder = new Texture2D(styleInsetSize, styleInsetSize);
        Color[] colors = new Color[styleInsetSize * styleInsetSize];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;
        whiteBorder.SetPixels(colors);
        whiteBorder.Apply();

        styleOutput = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGB32);
        styleOutput.wrapMode = TextureWrapMode.Clamp;
        styleOutput.filterMode = FilterMode.Bilinear;
    }

    private void PatchRuntimeWorkerWithStylePrediction()
    {
        Debug.Assert(worker != null);
        int savedAlphaBetasIndex = 0;
        for (int i = 0; i < layerNameToPatch.Count; ++i)
        {
            var tensors = worker.PeekConstants(layerNameToPatch[i]);
            int channels = predictionAlphasBetasData[savedAlphaBetasIndex].Length;
            for (int j = 0; j < channels; j++)
                tensors[0][j] = predictionAlphasBetasData[savedAlphaBetasIndex][j];
            for (int j = 0; j < channels; j++)
                tensors[1][j] = predictionAlphasBetasData[savedAlphaBetasIndex + 1][j];
            tensors[0].FlushCache(true);
            tensors[1].FlushCache(true);
            savedAlphaBetasIndex += 2;
        }
    }

    private void SetupBidirectionalReprojection()
    {
        setBidirWidth = targetCamera.pixelWidth;
        setBidirHeight = targetCamera.pixelHeight;
        setUseBidirectional = useFramerateUpsampling;

        targetCamera.depthTextureMode |= DepthTextureMode.MotionVectors;

        currentFrame = 0;

        // BIDIRECTIONAL ITERATIVE REPROJECTION SETUP
        if (iFrame0 != null)
            iFrame0.Release();
        if (iFrame1 != null)
            iFrame1.Release();
        if (iFrame2 != null)
            iFrame2.Release();
        if (bFrame != null)
            bFrame.Release();
        if (iFrame0SDMV != null)
            iFrame0SDMV.Release();
        if (iFrame1SDMV != null)
            iFrame1SDMV.Release();
        if (iFrame2SDMV != null)
            iFrame2SDMV.Release();

        iFrame0 = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGB32);
        iFrame0.wrapMode = TextureWrapMode.Clamp;
        iFrame0.filterMode = FilterMode.Bilinear;
        iFrame0.enableRandomWrite = true;
        iFrame0.useMipMap = false;
        iFrame0.Create();

        iFrame1 = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGB32);
        iFrame1.wrapMode = TextureWrapMode.Clamp;
        iFrame1.filterMode = FilterMode.Bilinear;
        iFrame1.enableRandomWrite = true;
        iFrame1.useMipMap = false;
        iFrame1.Create();

        iFrame2 = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGB32);
        iFrame2.wrapMode = TextureWrapMode.Clamp;
        iFrame2.filterMode = FilterMode.Bilinear;
        iFrame2.enableRandomWrite = true;
        iFrame2.useMipMap = false;
        iFrame2.Create();

        bFrame = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        bFrame.wrapMode = TextureWrapMode.Clamp;
        bFrame.filterMode = FilterMode.Bilinear;
        bFrame.enableRandomWrite = true;
        bFrame.useMipMap = false;
        bFrame.Create();

        iFrame0SDMV = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGBHalf);
        iFrame0SDMV.wrapMode = TextureWrapMode.Clamp;
        iFrame0SDMV.filterMode = FilterMode.Bilinear;
        iFrame0SDMV.enableRandomWrite = true;
        iFrame0SDMV.useMipMap = false;
        iFrame0SDMV.Create();

        iFrame1SDMV = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGBHalf);
        iFrame1SDMV.wrapMode = TextureWrapMode.Clamp;
        iFrame1SDMV.filterMode = FilterMode.Bilinear;
        iFrame1SDMV.enableRandomWrite = true;
        iFrame1SDMV.useMipMap = false;
        iFrame1SDMV.Create();

        iFrame2SDMV = new RenderTexture(setBidirWidth, setBidirHeight, 0, RenderTextureFormat.ARGBHalf);
        iFrame2SDMV.wrapMode = TextureWrapMode.Clamp;
        iFrame2SDMV.filterMode = FilterMode.Bilinear;
        iFrame2SDMV.enableRandomWrite = true;
        iFrame2SDMV.useMipMap = false;
        iFrame2SDMV.Create();
    }

    private void PrepareStylePrediction()
    {
        //Try to load style tensors from disk first
        StyleData loadedData = Resources.Load<StyleData>(GetStyleResourceName());
        if (loadedData)
        {
            predictionAlphasBetasData = new List<float[]>();
            for (int i = 0; i < loadedData.layerData.Count; i++)
            {
                predictionAlphasBetasData.Add(loadedData.layerData[i].data);
            }
            Resources.UnloadAsset(loadedData);
            return;
        }

        //Tensors not found on disk, compute them (and eventually store them on disk)
        Model tempModel = ModelLoader.Load(nnModel, verbose);
        List<Layer> predictionAlphasBetas = new List<Layer>();
        List<Layer> layerList = new List<Layer>(tempModel.layers);

        // Remove Divide by 255, Unity textures are in [0, 1] already
        int firstDivide = FindLayerIndexByName(layerList, "Style_Prediction_Network/normalized_image");
        layerList[firstDivide + 1].inputs[0] = layerList[firstDivide].inputs[0];
        layerList.RemoveAt(firstDivide);

        // Pre-process network to get it to run and extract Style alpha/beta tensors
        Layer lastConv = null;
        for (int i = 0; i < layerList.Count; i++)
        {
            Layer layer = layerList[i];

            // Remove Mirror padding layers (not supported, TODO)
            if (layer.name.Contains("reflect_padding"))
            {
                layerList[i + 1].inputs = layer.inputs;
                layerList[i + 1].pad = layer.pad.ToArray();
                layerList.RemoveAt(i);
                i--;
                continue;
            }
            // Placeholder instance norm bias + scale tensors
            if (layer.type == Layer.Type.Conv2D || layer.type == Layer.Type.Conv2DTrans)
            {
                lastConv = layer;
            }
            else if (layer.type == Layer.Type.Normalization)
            {
                int channels = lastConv.datasets[1].shape.channels;
                layer.datasets = new Layer.DataSet[2];

                layer.datasets[0].shape = new TensorShape(1, 1, 1, channels);
                layer.datasets[0].offset = 0;
                layer.datasets[0].length = channels;

                layer.datasets[1].shape = new TensorShape(1, 1, 1, channels);
                layer.datasets[1].offset = channels;
                layer.datasets[1].length = channels;

                layer.weights = new BarracudaArray(channels * 2);
                for (int j = 0; j < layer.weights.Length / 2; j++)
                    layer.weights[j] = 1.0f;
                for (int j = layer.weights.Length / 2; j < layer.weights.Length; j++)
                    layer.weights[j] = 0.0f;
            }

            if (layer.type != Layer.Type.StridedSlice && layer.name.Contains("StyleNetwork/"))
            {
                layerList.RemoveAt(i);
                i--;
            }

            if (layer.type == Layer.Type.StridedSlice)
            {
                predictionAlphasBetas.Add(layer);
            }
        }
        tempModel.layers = layerList;
        // Run Style_Prediction_Network on given style
        styleInput = new Tensor(StyleImage);
        CustomPinTensorFromTexture(styleInput);
        Dictionary<string, Tensor> temp = new Dictionary<string, Tensor>();
        temp.Add("frame", styleInput);
        temp.Add("style", styleInput);
        IWorker tempWorker = WorkerFactory.CreateWorker(WorkerFactory.ValidateType(internalSetup.workerType), tempModel, verbose);
        tempWorker.Execute(temp);

        // Store alpha/beta tensors from Style_Prediction_Network to feed into the run-time network
        predictionAlphasBetasData = new List<float[]>();
        for (int i = 0; i < predictionAlphasBetas.Count; i++)
        {
            Tensor O = tempWorker.PeekOutput(predictionAlphasBetas[i].name);
            predictionAlphasBetasData.Add(new float[O.length]);
            for (int j = 0; j < O.length; j++)
                predictionAlphasBetasData[i][j] = O[j];

            O.Dispose();
        }

        tempWorker.Dispose();

#if UNITY_EDITOR
        //Store to disk
        if (internalSetup.shouldSaveStyleTransferDataAsAsset)
        {
            SaveStyleTransferDataAsAsset();
        }
#endif
    }

    private string GetStyleResourceName()
    {
        return StyleImage.name + "_" + nnModel.name;
    }

#if UNITY_EDITOR
    //Save style data as asset
    private void SaveStyleTransferDataAsAsset()
    {
        StyleData dst = ScriptableObject.CreateInstance<StyleData>();
        for (int i = 0; i < predictionAlphasBetasData.Count; ++i)
        {
            var sData = new StyleDataLayer();
            sData.data = (float[])predictionAlphasBetasData[i].Clone();
            dst.layerData.Add(sData);
        }
        string fileName = GetStyleResourceName() + ".asset";
        string dataPath = "Assets/BarracudaStyleTransfer/Resources/" + fileName;
        if (AssetDatabase.FindAssets(fileName).Length == 0)
        {
            AssetDatabase.CreateAsset(dst, dataPath);
        }
    }
#endif

    private void CreateBarracudaWorker()
    {
        int savedAlphaBetasIndex = 0;
        model = ModelLoader.Load(nnModel, verbose);
        layerNameToPatch = new List<string>();
        List<Layer> layerList = new List<Layer>(model.layers);

        // Pre-process Network for run-time use
        Layer lastConv = null;
        for (int i = 0; i < layerList.Count; i++)
        {
            Layer layer = layerList[i];

            // Remove Style_Prediction_Network: constant with style, executed once in Setup()
            if (layer.name.Contains("Style_Prediction_Network/"))
            {
                layerList.RemoveAt(i);
                i--;
                continue;
            }

            // Fix Upsample2D size parameters
            if (layer.type == Layer.Type.Upsample2D)
            {
                layer.pool = new[] { 2, 2 };
                //ref model is supposed to be nearest sampling but bilinear scale better when network is applied at lower resoltions
                bool useBilinearUpsampling = internalSetup.forceBilinearUpsample2DInModel || (modelToUse != UsedModel.Reference);
                layer.axis = useBilinearUpsampling ? 1 : -1;
            }

            // Remove Mirror padding layers (not supported, TODO)
            if (layer.name.Contains("reflect_padding"))
            {
                layerList[i + 1].inputs = layer.inputs;
                layerList[i + 1].pad = layer.pad.ToArray();
                layerList.RemoveAt(i);
                i--;
            }
            else if (layer.type == Layer.Type.Conv2D || layer.type == Layer.Type.Conv2DTrans)
            {
                lastConv = layer;
            }
            else if (layer.type == Layer.Type.Normalization)
            {
                // Manually set alpha/betas from Style_Prediction_Network as scale/bias tensors for InstanceNormalization
                if (layerList[i - 1].type == Layer.Type.StridedSlice)
                {
                    int channels = predictionAlphasBetasData[savedAlphaBetasIndex].Length;
                    layer.datasets = new Layer.DataSet[2];

                    layer.datasets[0].shape = new TensorShape(1, 1, 1, channels);
                    layer.datasets[0].offset = 0;
                    layer.datasets[0].length = channels;

                    layer.datasets[1].shape = new TensorShape(1, 1, 1, channels);
                    layer.datasets[1].offset = channels;
                    layer.datasets[1].length = channels;

                    layerNameToPatch.Add(layer.name);

                    layer.weights = new BarracudaArray(channels * 2);
                    for (int j = 0; j < layer.weights.Length / 2; j++)
                        layer.weights[j] = predictionAlphasBetasData[savedAlphaBetasIndex][j];
                    for (int j = layer.weights.Length / 2; j < layer.weights.Length; j++)
                        layer.weights[j] = predictionAlphasBetasData[savedAlphaBetasIndex + 1][j - layer.weights.Length / 2];

                    savedAlphaBetasIndex += 2;
                }
                // Else initialize scale/bias tensors of InstanceNormalization to default 1/0
                else
                {
                    int channels = lastConv.datasets[1].shape.channels;
                    layer.datasets = new Layer.DataSet[2];

                    layer.datasets[0].shape = new TensorShape(1, 1, 1, channels);
                    layer.datasets[0].offset = 0;
                    layer.datasets[0].length = channels;

                    layer.datasets[1].shape = new TensorShape(1, 1, 1, channels);
                    layer.datasets[1].offset = channels;
                    layer.datasets[1].length = channels;

                    layer.weights = new BarracudaArray(channels * 2);
                    for (int j = 0; j < layer.weights.Length / 2; j++)
                        layer.weights[j] = 1.0f;
                    for (int j = layer.weights.Length / 2; j < layer.weights.Length; j++)
                        layer.weights[j] = 0.0f;
                }
            }
        }

        // Remove Slice layers originally used to get alpha/beta tensors into Style_Network
        for (int i = 0; i < layerList.Count; i++)
        {
            Layer layer = layerList[i];
            if (layer.type == Layer.Type.StridedSlice)
            {
                layerList.RemoveAt(i);
                i--;
            }
        }

        // Fold Relu into instance normalisation
        Dictionary<string, string> reluToInstNorm = new Dictionary<string, string>();
        for (int i = 0; i < layerList.Count; i++)
        {
            Layer layer = layerList[i];
            if (layer.type == Layer.Type.Activation && layer.activation == Layer.Activation.Relu)
            {
                if (layerList[i - 1].type == Layer.Type.Normalization)
                {
                    layerList[i - 1].activation = layer.activation;
                    reluToInstNorm[layer.name] = layerList[i - 1].name;
                    layerList.RemoveAt(i);
                    i--;
                }
            }
        }
        for (int i = 0; i < layerList.Count; i++)
        {
            Layer layer = layerList[i];
            for (int j = 0; j < layer.inputs.Length; j++)
            {
                if (reluToInstNorm.ContainsKey(layer.inputs[j]))
                {
                    layer.inputs[j] = reluToInstNorm[layer.inputs[j]];
                }
            }
        }

        // Feed first convolution directly with input (no need for normalisation from the model)
        string firstConvName = "StyleNetwork/conv1/convolution_conv1/convolution";
        int firstConv = FindLayerIndexByName(layerList, firstConvName);
        layerList[firstConv].inputs = new[] { model.inputs[1].name };

        if (modelToUse == UsedModel.Reference)
        {
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/add"));
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/add/y"));
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/normalized_contentFrames"));
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/normalized_contentFrames/y"));
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/sub"));
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/sub/y"));
        }
        if (modelToUse == UsedModel.RefBut32Channels)
        {
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalized_contentFrames"));
            layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalized_contentFrames/y"));
        }

        // Remove final model post processing, post process happen in tensor to texture instead
        int postAdd = FindLayerIndexByName(layerList, "StyleNetwork/clamp_0_255/add");
        layerList.RemoveRange(postAdd, 5);

        // Correct wrong output layer list
        model.outputs = new List<string>() { layerList[postAdd - 1].name };

        model.layers = layerList;
        Model.Input input = model.inputs[1];
        input.shape[0] = 0;
        input.shape[1] = 1080;//TODO get framebuffer size rather than hardcoded value
        input.shape[2] = 1920;
        input.shape[3] = 3;
        model.inputs = new List<Model.Input> { model.inputs[1] };
        //Create worker and execute it once at target resolution to prime all memory allocation (however in editor resolution can still change at runtime)
        worker = WorkerFactory.CreateWorker(WorkerFactory.ValidateType(internalSetup.workerType), model, verbose);
        Dictionary<string, Tensor> temp = new Dictionary<string, Tensor>();
        var inputTensor = new Tensor(input.shape, input.name);
        temp.Add("frame", inputTensor);
        worker.Execute(temp);
        inputTensor.Dispose();
    }

    private int FindLayerIndexByName(List<Layer> list, string name)
    {
        int res = 0;
        while (res < list.Count && list[res].name != name)
            res++;
        return res;
    }
    
    private class CustomComputeKernel
    {
        public readonly int kernelIndex;
        public readonly ComputeShader shader;

        private readonly string kernelName;
        private readonly uint threadGroupSizeX;
        private readonly uint threadGroupSizeY;
        private readonly uint threadGroupSizeZ;
        
        public CustomComputeKernel(ComputeShader cs, string kn)
        {
            string kernelNameWithChannelsOrder = kn + (ComputeInfo.channelsOrder == ComputeInfo.ChannelsOrder.NHWC ? "_NHWC" : "_NCHW");
            if (!cs.HasKernel(kernelNameWithChannelsOrder) && !cs.HasKernel(kn))
                throw new ArgumentException($"Kernel {kn} and {kernelNameWithChannelsOrder} are both missing");
            
            shader = cs;
            kernelName = cs.HasKernel(kernelNameWithChannelsOrder)?kernelNameWithChannelsOrder:kn;
            kernelIndex = shader.FindKernel(kernelName);
            shader.GetKernelThreadGroupSizes(kernelIndex, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);
        }
        
        private static int IntDivCeil(int v, int div)
        {
            return (v + div - 1) / div;
        }
        
        public void Dispatch(int workItemsX, int workItemsY, int workItemsZ)
        {
            Profiler.BeginSample(kernelName);
            var x = IntDivCeil(workItemsX, (int) threadGroupSizeX);
            var y = IntDivCeil(workItemsY, (int) threadGroupSizeY);
            var z = IntDivCeil(workItemsZ, (int) threadGroupSizeZ);
            shader.Dispatch(kernelIndex, x, y, z);
            Profiler.EndSample();
        }
        
        public void SetTensor(string name, TensorShape shape, ComputeBuffer buffer, Int64 dataOffset = 0)
        {
            var shapeId = Shader.PropertyToID(name + "declShape");
            var infoId = Shader.PropertyToID(name + "declInfo");
            var dataId = Shader.PropertyToID(name + "data");
            int[] tensorShape = {shape.batch, shape.height, shape.width, shape.channels};
            int[] tensorInfo = {(int)dataOffset, shape.length};
            shader.SetInts(shapeId, tensorShape);
            shader.SetInts(infoId, tensorInfo);
            shader.SetBuffer(kernelIndex, dataId, buffer);
        }
    }

    private void CustomTensorToRenderTexture(Tensor X, RenderTexture target, int batch, int fromChannel, Vector4 scale, Vector4 bias, Texture3D lut = null)
    {
        if (!internalSetup.shouldUseSRGBTensor)
        {
            X.ToRenderTexture(target, batch, fromChannel, scale, bias, lut);
            return;
        }

        //By default Barracuda work on Tensor containing value in linear color space.
        //Here we handle custom convertion from tensor to texture when tensor is in sRGB color space.
        //This is important for this demo as network was trained with data is sRGB color space.
        //Direct support for this will be added in a latter revision of Barracuda.
        if (!target.enableRandomWrite || !target.IsCreated())
        {
            target.Release();
            target.enableRandomWrite = true;
            target.Create();
        }

        var gpuBackend = new ReferenceComputeOps();
        var fn = new CustomComputeKernel(tensorToTextureSRGB, "TensorToTexture"+ (lut == null?"NoLUT":"3DLUT"));
        var XonDevice = gpuBackend.Pin(X);
        fn.SetTensor("X", X.shape, XonDevice.buffer, XonDevice.offset);
        fn.shader.SetTexture(fn.kernelIndex, "Otex2D", target);
        fn.shader.SetVector("_Scale", scale);
        fn.shader.SetVector("_Bias", bias);
        fn.shader.SetInts("_Pad", new int[] { batch, 0, 0, fromChannel });
        fn.shader.SetBool("_FlipY", true);
        if (lut != null)
        {
            fn.shader.SetTexture(fn.kernelIndex, "Otex3D", lut);
            fn.shader.SetVector("_LutParams", new Vector2(1f / lut.width, lut.width - 1f));
        }

        fn.Dispatch(target.width, target.height, 1);
    }

    void CustomPinTensorFromTexture(Tensor X)
    {
        if (!internalSetup.shouldUseSRGBTensor)
            return;

        //By default Barracuda work on Tensor containing value in linear color space.
        //Here we handle custom tensor Pin from texture when tensor is to contain data in sRGB color space.
        //This is important for this demo as network was trained with data is sRGB color space.
        //Direct support for this will be added in a latter revision of Barracuda.
        var onDevice = X.tensorOnDevice as ComputeTensorData;
        Debug.Assert(onDevice == null);

        var asTexture = X.tensorOnDevice as TextureAsTensorData;
        Debug.Assert(asTexture != null);

        X.AttachToDevice(CustomTextureToTensorData(asTexture, X.name));
    }
    
    ITensorData CustomTextureToTensorData(TextureAsTensorData texData, string name)
    {
        //By default Barracuda work on Tensor containing value in linear color space.
        //Here we handle custom tensor Pin from texture when tensor is to contain data in sRGB color space.
        //This is important for this demo as network was trained with data is sRGB color space.
        //Direct support for this will be added in a latter revision of Barracuda. 
        var fn = new CustomComputeKernel(tensorToTextureSRGB, "TextureToTensor");
        var tensorData = new ComputeTensorData(texData.shape, name, ComputeInfo.channelsOrder, false);

        fn.SetTensor("O", texData.shape, tensorData.buffer);
        fn.shader.SetBool("_FlipY", texData.flip == TextureAsTensorData.Flip.Y);

        var offsets = new int[] { 0,0,0,0 };
        foreach (var tex in texData.textures)
        {
            var texArr = tex as Texture2DArray;
            var tex3D = tex as Texture3D;
            var rt = tex as RenderTexture;

            var texDepth = 1;
            if (texArr)
                texDepth = texArr.depth;
            else if (tex3D)
                texDepth = tex3D.depth;
            else if (rt)
                texDepth = rt.volumeDepth;

            //var srcChannelMask = TextureFormatUtils.FormatToChannelMask(tex, texData.interpretPixelAsChannels);
            Color srcChannelMask = Color.white;

            fn.shader.SetTexture(fn.kernelIndex, "Xtex2D", tex);
            fn.shader.SetInts("_Pool", new int [] {tex.width, tex.height});
            fn.shader.SetInts("_Pad", offsets);
            fn.shader.SetInts("_ChannelWriteMask", new [] {(int)srcChannelMask[0], (int)srcChannelMask[1], (int)srcChannelMask[2], (int)srcChannelMask[3] });

            fn.Dispatch(texData.shape.width, texData.shape.height, texDepth);

            if (texData.interpretDepthAs == TextureAsTensorData.InterpretDepthAs.Batch)
                offsets[0] += texDepth;
            else if (texData.interpretDepthAs == TextureAsTensorData.InterpretDepthAs.Channels)
                offsets[3] += texDepth * texData.interpretPixelAsChannels;
        }

        return tensorData;
    }
    
    [PostProcessScene]
    public static void OnPostprocessScene()
    {
        #if !UNITY_STANDALONE && !UNITY_EDITOR
            throw new Exception("PLATFORM NOT SUPPORTED BY THIS DEMO");
        #endif
    }
}
