using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

//
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.AI.MachineLearning.Preview;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.System.Threading;
using Windows.UI.Core;


// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x411 を参照してください

namespace WindowsML_MobileNet
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private MediaCapture mediaCapture;
        private ThreadPoolTimer timer;
        private SemaphoreSlim semaphore = new SemaphoreSlim(1);

        private LearningModelPreview model = null;
        private const string ModelFileName = "MobileNet.onnx";
        private ImageVariableDescriptorPreview inputImageDescription;
        private MapVariableDescriptorPreview outputMapDescription;
        private TensorVariableDescriptorPreview outputTensorDescription;
        private IDictionary<string, float> prob { get; set; }
        private IList<string> classLabel { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await InitCameraAsync();
            await LoadModelAsync();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task LoadModelAsync()
        {
            if (model != null) return;

            try
            {
                //Outputを受け取るためには予め領域の確保が必要。
                prob = new Dictionary<string, float>();

                for (var i = 0; i < 1000; i++)
                {
                    prob.Add(i.ToString(), float.NaN);
                }
                
                //Load Model
                var modelFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///Assets/{ModelFileName}"));
                model = await LearningModelPreview.LoadModelFromStorageFileAsync(modelFile);

                // Retrieve model input and output variable descriptions (we already know the model takes an image in and outputs a tensor)
                List<ILearningModelVariableDescriptorPreview> inputFeatures = model.Description.InputFeatures.ToList();
                List<ILearningModelVariableDescriptorPreview> outputFeatures = model.Description.OutputFeatures.ToList();

                
                inputImageDescription =
                    inputFeatures.FirstOrDefault(feature => feature.ModelFeatureKind == LearningModelFeatureKindPreview.Image)
                    as ImageVariableDescriptorPreview;

                outputMapDescription =
                    outputFeatures.FirstOrDefault(feature => feature.ModelFeatureKind == LearningModelFeatureKindPreview.Map)
                    as MapVariableDescriptorPreview;

                outputTensorDescription =
                    outputFeatures.FirstOrDefault(feature => feature.ModelFeatureKind == LearningModelFeatureKindPreview.Tensor)
                    as TensorVariableDescriptorPreview;

                //オプション設定
                model.InferencingOptions.ReclaimMemoryAfterEvaluation = true;

                //GPUを設定すると処理速度が上がるけれど、現時点では途中でエラーになってモデルが停止する。
                //model.InferencingOptions.PreferredDeviceKind = LearningModelDeviceKindPreview.LearningDeviceGpu;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => statusTBlock.Text = $"Loaded MobileNet.onnx");

            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => statusTBlock.Text = $"error: {ex.Message}");
                model = null;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task InitCameraAsync()
        {
            Debug.WriteLine("InitializeCameraAsync");

            try
            {
                //mediaCaptureオブジェクトが有効な時は一度Disposeする
                if (mediaCapture != null)
                {
                    mediaCapture.Dispose();
                    mediaCapture = null;
                }

                //キャプチャーの設定
                var captureInitSettings = new MediaCaptureInitializationSettings();
                captureInitSettings.VideoDeviceId = "";
                captureInitSettings.StreamingCaptureMode = StreamingCaptureMode.Video;

                //カメラデバイスの取得
                var cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

                if (cameraDevices.Count() == 0)
                {
                    Debug.WriteLine("No Camera");
                    return;
                }
                else if (cameraDevices.Count() == 1)
                {
                    captureInitSettings.VideoDeviceId = cameraDevices[0].Id;
                }
                else
                {
                    captureInitSettings.VideoDeviceId = cameraDevices[1].Id;
                }

                //キャプチャーの準備
                mediaCapture = new MediaCapture();
                await mediaCapture.InitializeAsync(captureInitSettings);


                VideoEncodingProperties vp = new VideoEncodingProperties();

                //Windows IoT Coreで実行するために解像度を低めにしている。適宜変更可能。
                vp.Height = 240;
                vp.Width = 320;

                //カメラによって利用できるSubtypeに違いがあるので、利用できる解像度の内最初に見つかった組み合わせのSubtypeを選択する。
                var resolusions = GetPreviewResolusions(mediaCapture);
                vp.Subtype = resolusions.Find(subtype => subtype.Width == vp.Width).Subtype;

                await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, vp);

                capture.Source = mediaCapture;

                //キャプチャーの開始
                await mediaCapture.StartPreviewAsync();

                Debug.WriteLine("Camera Initialized");

                //15FPS毎にタイマーを起動する。
                TimeSpan timerInterval = TimeSpan.FromMilliseconds(66);
                timer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(CurrentVideoFrame), timerInterval);
            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => statusTBlock.Text = $"error: {ex.Message}");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="capture"></param>
        /// <returns></returns>
        private List<VideoEncodingProperties> GetPreviewResolusions(MediaCapture capture)
        {
            IReadOnlyList<IMediaEncodingProperties> ret;
            ret = capture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview);

            if (ret.Count <= 0)
            {
                return new List<VideoEncodingProperties>();
            }


            //接続しているカメラの対応解像度やSubtypeを確認するときはコメントを外す
            /*
            foreach (VideoEncodingProperties vp in ret)
            {
                var frameRate = (vp.FrameRate.Numerator / vp.FrameRate.Denominator);

                Debug.WriteLine("{0}: {1}x{2} {3}fps", vp.Subtype, vp.Width, vp.Height, frameRate);

            }
            */

            return ret.Select(item => (VideoEncodingProperties)item).ToList();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="timer"></param>
        private async void CurrentVideoFrame(ThreadPoolTimer timer)
        {
            if (!semaphore.Wait(0)) return;

            try
            {
                //モデルへの入力はBGRA8形式、解像度224x224が指定されている
                VideoFrame previewFrame = new VideoFrame(BitmapPixelFormat.Bgra8, 224, 224);
                await mediaCapture.GetPreviewFrameAsync(previewFrame);
                await EvaluateVideoFrameAsync(previewFrame);

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    previewFrame.Dispose();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                semaphore.Release();
            }

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="previewFrame"></param>
        /// <returns></returns>
        private async Task EvaluateVideoFrameAsync(VideoFrame previewFrame)
        {
            if (previewFrame != null)
            {
                try
                {
                    IList<string> classLabel = new List<string>();
                    LearningModelBindingPreview binding = new LearningModelBindingPreview(model as LearningModelPreview);
                    binding.Bind(inputImageDescription.Name, previewFrame);
                    binding.Bind(outputMapDescription.Name, prob);
                    binding.Bind(outputTensorDescription.Name, classLabel);

                    var stopwatch = Stopwatch.StartNew();
                    LearningModelEvaluationResultPreview results = await model.EvaluateAsync(binding, string.Empty);
                    stopwatch.Stop();

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        fpsTBlock.Text = $"{1000f / stopwatch.ElapsedMilliseconds,4:f1} fps";
                        statusTBlock.Text = classLabel[0];

                    });

                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }
        }
    }
}
