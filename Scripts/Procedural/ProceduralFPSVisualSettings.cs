using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Один компонент для всей картинки процедурного FPS: фонарь, темнота мира,
/// тени, пост-обработка URP и PS1-фильтр. Вешается на объект Player.
/// Все поля с русскими подсказками — наведи мышку на название поля в инспекторе.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]
[ExecuteAlways]
public sealed class ProceduralFPSVisualSettings : MonoBehaviour
{
    [Header("ССЫЛКИ (пустые — найдутся сами)")]
    [Tooltip("Контроллер игрока. Если пусто — берётся с этого же объекта.")]
    public ProceduralFirstPersonController player;
    [Tooltip("Камера от первого лица. Если пусто — берётся у игрока или Camera.main.")]
    public Camera playerCamera;
    [Tooltip("Фонарь (Spot Light). Если пусто — ищется среди детей камеры.")]
    public Light flashlight;
    [Tooltip("PS1-фильтр (пикселизация) — обычно на камере.")]
    public Ps1Filter ps1Filter;
    [Tooltip("Глобальный Volume с пост-эффектами (bloom, виньетка и т.д.).")]
    public Volume postProcessVolume;

    [Header("КАМЕРА / ЖИВОСТЬ (bodycam-lite)")]
    [Tooltip("Скорость «догоняния» взгляда камерой. Больше = чётче отслеживание мыши; меньше (8–12) = камера резиново отстаёт и догоняет — основной вкус bodycam. Слишком мало = укачивает.")]
    [Range(4f, 40f)] public float lookLagSpeed = 13f;
    [Tooltip("Микро-повороты камеры по горизонтали от «дрожи рук» (градусы). Даёт эффект ручной съёмки. 0 = идеально ровная как штатив.")]
    [Range(0f, 2f)] public float handheldYawNoise = 0.45f;
    [Tooltip("Микро-повороты камеры по вертикали от «дрожи рук» (градусы).")]
    [Range(0f, 2f)] public float handheldPitchNoise = 0.3f;
    [Tooltip("Скорость дрожания рук. Меньше = ленивое плавание камеры, больше = нервная.")]
    [Range(0.2f, 4f)] public float handheldNoiseSpeed = 1.1f;
    [Tooltip("Амплитуда «дыхания» в покое (метры): камера слегка плавает даже когда стоишь. Главный источник живости без движения.")]
    [Range(0f, 0.05f)] public float idleSway = 0.007f;
    [Tooltip("Скорость дыхания в покое. Меньше = медленное спокойное плавание.")]
    [Range(0.2f, 3f)] public float idleSwaySpeed = 0.8f;
    [Tooltip("Сила покачивания от шагов (метры). Вертикальный боб + горизонтальная «восьмёрка».")]
    [Range(0f, 0.08f)] public float bobStrength = 0.022f;
    [Tooltip("Длина шага при ходьбе (метры). Темп шагов = скорость / длина шага: быстрее идёшь — чаще шаги; звук, боб и скорость всегда в такт.")]
    [Range(0.5f, 1.4f)] public float strideWalk = 0.85f;
    [Tooltip("Длина шага при беге (метры). Больше = шире и реже шаги на бегу, но каденс всё равно выше ходьбы за счёт скорости.")]
    [Range(0.8f, 2.2f)] public float strideRun = 1.3f;
    [Tooltip("Насколько боковая «восьмёрка» боба шире вертикали (0 = боб только вверх-вниз).")]
    [Range(0f, 1.5f)] public float bobSideSwing = 0.55f;
    [Tooltip("Множитель боба на бегу: 1 = боб как на шаге, 1.4+ = заметно трясёт на бегу.")]
    [Range(1f, 3f)] public float bobRunBoost = 1.4f;
    [Tooltip("Камера-тилт от поворота мыши: голова слегка наклоняется В СТОРОНУ поворота (градусы на градус/сек угловой скорости). Если наклон не в ту сторону — поставь отрицательное значение.")]
    [Range(-0.5f, 0.5f)] public float lookTiltScale = 0.08f;
    [Tooltip("Максимум тилта от поворота (градусы), чтобы резкие рывки мыши не заваливали горизонт.")]
    [Range(0f, 5f)] public float lookTiltMax = 1.5f;
    [Tooltip("Крен камеры от скорости бега (градусы на м/с). Лёгкий наклон вбок при движении вперёд.")]
    [Range(0f, 2f)] public float speedRollScale = 0.22f;
    [Tooltip("Крен при стрейфах (градусы на м/с боковой скорости). Камера наклоняется как голова при переступании.")]
    [Range(0f, 2f)] public float strafeRollScale = 0.4f;
    [Tooltip("Клевок камеры при рывке/торможении (градусы на м/с²). Разгон — камера чуть вверх, стоп — вниз.")]
    [Range(0f, 3f)] public float accelPitchScale = 0.55f;
    [Tooltip("Максимум клевка при ускорении (градусы), чтобы рывки не выворачивали камеру.")]
    [Range(0f, 5f)] public float accelPitchMax = 1.3f;
    [Tooltip("Нырок камеры при жёстком приземлении (метры на м/с скорости падения).")]
    [Range(0f, 0.2f)] public float landingDipScale = 0.03f;
    [Tooltip("Максимальная глубина нырка при приземлении (метры).")]
    [Range(0f, 0.5f)] public float landingDipMax = 0.09f;
    [Tooltip("Как быстро камера поднимается обратно после нырка (больше = резче отскок).")]
    [Range(1f, 12f)] public float landingRecoverSpeed = 4.5f;
    [Tooltip("Расширение FOV на бегу (градусы) — ощущение скорости. 0 = FOV всегда постоянный.")]
    [Range(0f, 15f)] public float sprintFovBoost = 4f;
    [Tooltip("Скорость FOV-КИКА при старте бега (быстрое расширение = пинок). Больше = резче пинок.")]
    [Range(1f, 30f)] public float fovKickInSpeed = 14f;
    [Tooltip("Скорость возврата FOV после остановки бега (медленнее входа = мягкое выдохновение).")]
    [Range(1f, 30f)] public float fovKickOutSpeed = 6f;

    [Header("ДВИЖЕНИЕ / ИНЕРЦИЯ")]
    [Tooltip("Разгон движения (м/с²). Меньше = персонаж «плывёт» и набирает ход плавно; больше = отзывчивее. ~16 = лёгкая инерция.")]
    [Range(4f, 60f)] public float moveAcceleration = 16f;
    [Tooltip("Торможение при отпускании клавиш (м/с²). Меньше = дольше скользишь после остановки.")]
    [Range(4f, 80f)] public float moveDeceleration = 22f;
    [Tooltip("Управляемость в воздухе (0..1 от наземного разгона). 0.35 = в прыжке/падении инерция сохраняется.")]
    [Range(0f, 1f)] public float airControl = 0.35f;

    [Header("НЕБО")]
    [Tooltip("Чёрное ночное небо: убирает стандартный Unity-скайбокс (он ЯРКИЙ — на крышах и открытых комнатах без потолка он заливает экран белым светом и блумом). Небо становится чёрным, как и положено ночью.")]
    public bool blackNightSky = true;

    [Header("ГЛУБИНА РЕЗКОСТИ (DoF)")]
    [Tooltip("Включён ли Depth of Field: то, на что смотришь — резко, передний план и дали мягко размываются. На PS1-пикселях выглядит как аутентичное «мыло» объектива.")]
    public bool dofEnabled = true;
    [Tooltip("Тип размытия. Bokeh = классическое с бликами; Gaussian = мягкое дешёвое (на PS1-стиле обычно приятнее).")]
    public DepthOfFieldMode dofMode = DepthOfFieldMode.Bokeh;
    [Tooltip("Дистанция фокуса (метры): всё на этом расстоянии резко. Также база для автофокуса.")]
    [Range(0.3f, 15f)] public float dofFocusDistance = 2.6f;
    [Tooltip("Диафрагма (f-стоп): МЕНЬШЕ = сильнее размытие. 4.5 = деликатно, 1.4 = жирное кино-мыло.")]
    [Range(1.2f, 32f)] public float dofAperture = 4.5f;
    [Tooltip("Фокусное расстояние объектива (мм): влияет на масштаб размытия по краям. 40–60 = нейтрально.")]
    [Range(10f, 300f)] public float dofFocalLength = 55f;
    [Tooltip("Автофокус: дистанция фокуса сама подтягивается к тому, куда смотришь (рейкаст из центра экрана). Стена, на которую смотришь, становится резкой.")]
    public bool dofAutoFocus = true;
    [Tooltip("Скорость автофокуса. Больше = быстрее перестраивается, меньше = кинематографично «плывёт» резкость.")]
    [Range(1f, 20f)] public float dofAutoFocusSpeed = 7f;

    [Header("ИНВЕНТАРЬ (TAB)")]
    [Tooltip("Сколько ячеек по горизонтали.")]
    [Range(2, 10)] public int inventoryColumns = 5;
    [Tooltip("Сколько ячеек по вертикали.")]
    [Range(2, 14)] public int inventoryRows = 8;
    [Tooltip("Размер одной ячейки (пиксели по референсу 1920×1080).")]
    [Range(32f, 128f)] public float inventoryCellSize = 78f;
    [Tooltip("Зазор между ячейками (пиксели).")]
    [Range(0f, 16f)] public float inventoryCellGap = 5f;
    [Tooltip("Скорость выезда/уездания панели. Больше = быстрее.")]
    [Range(2f, 30f)] public float inventorySlideSpeed = 8f;
    [Tooltip("Непрозрачность основной панели инвентаря. Меньше = сильнее виден размытый мир под ней.")]
    [Range(0.2f, 1f)] public float inventoryPanelOpacity = 0.76f;
    [Tooltip("Непрозрачность ячеек инвентаря.")]
    [Range(0.2f, 1f)] public float inventoryCellOpacity = 0.78f;
    [Tooltip("Затемнение размытого фона под инвентарём.")]
    [Range(0f, 0.65f)] public float inventoryBackdropDim = 0.22f;
    [Tooltip("Сила размытия фона инвентаря. 1 = мягко, 4 = заметно сильнее.")]
    [Range(0.5f, 5f)] public float inventoryBackdropBlur = 2.5f;
    [Tooltip("Контраст живого размытого фона под инвентарём. 1 = без изменений; 0.9 = немного мягче.")]
    [Range(0.5f, 1.2f)] public float inventoryBackdropContrast = 0.92f;
    [Tooltip("Пока инвентарь открыт, отключает PS1-дизеринг/зерно только на фоне инвентаря. При закрытии фильтр включается обратно.")]
    public bool inventoryBackdropBypassPs1Filter = true;
    [Tooltip("Во сколько раз уменьшать снимок экрана перед размытием. Больше = дешевле и мягче.")]
    [Range(2, 10)] public int inventoryBlurDownsample = 5;
    [Tooltip("Отступ инвентаря от левого края экрана (пиксели по референсу 1920×1080).")]
    [Range(0f, 100f)] public float inventoryLeftInset = 38f;
    [Tooltip("Цвет фона панели.")]
    public Color inventoryPanelColor = new Color(0.03f, 0.03f, 0.035f, 0.94f);
    [Tooltip("Цвет ячеек.")]
    public Color inventoryCellColor = new Color(0.10f, 0.10f, 0.11f, 1f);

    [Header("ПЛАВНОСТЬ И КОМФОРТ")]
    [Tooltip("Вертикальная синхронизация: 1 = ждать монитор (обязательно для плавности, убирает разрывы кадра), 2 = через кадр (полсекции), 0 = выкл (не рекомендую).")]
    [Range(0, 2)] public int vsyncCount = 1;
    [Tooltip("Лимит FPS: 0 = без лимита (рулит всинк). Полезно поставить 60–120, если всинк выключен или монитор 144+ и хочется стабильного шага физики.")]
    [Range(0, 240)] public int targetFrameRate = 0;
    [Tooltip("Сглаживание мыши: 0 = сырой вход (микродёргания на высоком FPS), 0.3–0.5 = шелковистый ход мыши. Вместе с Look Lag Speed даёт мягкую картинку.")]
    [Range(0f, 1f)] public float mouseSmoothing = 0.3f;
    [Tooltip("HDR-вывод на монитор. НА SDR-МОНТОРЕ ДЕРЖАТЬ ВЫКЛЮЧЁННЫМ: пересветы, странные цвета и усталость глаз. Включай только если у тебя настоящий HDR-экран.")]
    public bool allowHdROutput = false;
    [Tooltip("Сглаживание краёв ДО пикселизации: FXAA мягче и дешевле, SMAA чётче. None оставляет грубые ступеньки.")]
    public AntialiasingMode antialiasingMode = AntialiasingMode.FastApproximateAntialiasing;
    [Tooltip("Качество FXAA/SMAA. High немного дороже, но лучше сглаживает диагональные края.")]
    public AntialiasingQuality antialiasingQuality = AntialiasingQuality.High;
    [Tooltip("Дополнительное сглаживание ступенек PS1-фильтра. 0 = жёсткие пиксели, 1 = максимально мягкие края. Начни с 0.25–0.4.")]
    [Range(0f, 1f)] public float ps1EdgeSmoothing = 0.32f;
    [Tooltip("Motion Blur — капелька смазывания при поворотах. Больше 0.3 уже качает; 0.1–0.15 = едва заметная плавность.")]
    public bool motionBlurEnabled = true;
    [Tooltip("Сила моушнблюра. 0.15 = «совсем капелька», как просили.")]
    [Range(0f, 1f)] public float motionBlurIntensity = 0.15f;
    [Tooltip("Максимальный смаз в доле экрана за кадр — страховка от «мыла» при резком повороте. 0.04 = максимум пара пикселей PS1.")]
    [Range(0f, 0.2f)] public float motionBlurClamp = 0.04f;

    [Header("КАМЕРА")]
    [Tooltip("Применять настройки ниже к камере. Выключи, если хочешь настраивать камеру вручную.")]
    public bool overrideCameraSettings = true;
    [Tooltip("Угол обзора в градусах. Больше = шире вид, по краям сильнее искажение.")]
    [Range(50f, 110f)] public float cameraFieldOfView = 76f;
    [Tooltip("Ближняя плоскость отсечения (метры). Всё ближе этой дистанции не рисуется. Меньше = можно подносить объекты к лицу.")]
    [Min(0.001f)] public float cameraNearClip = 0.035f;
    [Tooltip("Дальняя плоскость отсечения (метры). Всё дальше не рисуется.")]
    [Min(10f)] public float cameraFarClip = 100f;
    [Tooltip("Цвет пустоты (куда не попадает геометрия). В нашей ночной сцене это почти чёрный.")]
    public Color cameraBackgroundColor = new Color(0.012f, 0.014f, 0.018f, 1f);
    [Tooltip("HDR-рендер камеры: больше диапазона яркости для bloom/тонмаппинга. Обычно держать включённым.")]
    public bool cameraHDR = true;

    [Header("ФОНАРЬ (обычный Unity Spot Light)")]
    [Tooltip("Включён ли фонарь при старте игры. В игре переключается клавишей F.")]
    public bool flashlightStartsOn = false;
    [Tooltip("Цвет луча. Тёплый (1, 0.91, 0.76) = лампочка; холодный (0.85, 0.92, 1) = ледяной ночник.")]
    public Color flashlightColor = new Color(1f, 0.91f, 0.76f, 1f);
    [Tooltip("Яркость фонаря. Если световое пятно выбеливает текстуры в кашу — снижай (4–6).")]
    [Min(0f)] public float flashlightIntensity = 10f;
    [Tooltip("Дальность луча в метрах. Дальше этого фонарь не достаёт.")]
    [Min(0.1f)] public float flashlightRange = 13f;
    [Tooltip("Внешний угол конуса света (градусы). Больше = шире пятно, свет размазаннее.")]
    [Range(10f, 120f)] public float flashlightSpotAngle = 72f;
    [Tooltip("Внутренний угол (градусы) — центральное яркое пятно внутри конуса. Всегда меньше внешнего.")]
    [Range(1f, 120f)] public float flashlightInnerSpotAngle = 38f;
    [Tooltip("Смещение фонаря относительно камеры (локально): вправо/вниз/вперёд в метрах. Имитирует фонарь в руке.")]
    public Vector3 flashlightLocalOffset = new Vector3(0.045f, -0.035f, 0.10f);
    [Tooltip("Дополнительный поворот луча (градусы, локально). 0 = светит ровно куда смотрит камера.")]
    public Vector3 flashlightRotationOffset = Vector3.zero;
    [Tooltip("Бросает ли фонарь тени. Тени красивые, но при больших смещениях биасов могут съедать свет вблизи стен.")]
    public bool flashlightCastsShadows = true;
    [Tooltip("Насколько тени чёрные: 1 = абсолютная тень, меньше = свет просачивается в тень.")]
    [Range(0f, 1f)] public float flashlightShadowStrength = 0.85f;
    [Tooltip("Смещение тени (борьба с полосами-«акне» на стенах). Слишком много = тени отползают от предметов.")]
    [Range(0f, 2f)] public float flashlightShadowBias = 0.05f;
    [Tooltip("Сдвиг точки приёмника тени вдоль нормали. ВАЖНО: больше толщины стен (0.22) = свет near стен может пропадать. Держи ~0.1–0.2.")]
    [Range(0f, 2f)] public float flashlightShadowNormalBias = 0.35f;
    [Tooltip("Ближняя плоскость теневой карты фонаря (метры). Всё ближе не отбрасывает тень.")]
    [Min(0.01f)] public float flashlightShadowNearPlane = 0.08f;

    [Header("СВЕТ МИРА / ТЕМНОТА НОЧИ")]
    [Tooltip("Применять настройки ambient-освещения ниже.")]
    public bool overrideAmbientLighting = true;
    [Tooltip("Цвет ровной подсветки всего мира (ambient). Умножается на Intensity ниже.")]
    public Color ambientColor = new Color(0.012f, 0.012f, 0.016f, 1f);
    [Tooltip("Сила ambient. 0.12 × цвет выше = едва заметная синяя мгла. Больше = видно без фонаря, меньше = кромешная тьма.")]
    [Range(0f, 2f)] public float ambientIntensity = 0.12f;
    [Tooltip("Применять настройки ко всем направленным светам в сцене (солнце/луна).")]
    public bool overrideDirectionalLights = true;
    [Tooltip("Цвет «лунного» света.")]
    public Color directionalLightColor = new Color(0.62f, 0.67f, 0.78f, 1f);
    [Tooltip("Яркость направленного света. 0.008 = почти ноль (лунная ночь). Больше = мягкое общее освещение без фонаря.")]
    [Range(0f, 2f)] public float directionalLightIntensity = 0.008f;
    [Tooltip("Использовать отражения окружения (skybox) на гладких материалах. У нас материалы матовые — можно не включать.")]
    public bool useEnvironmentReflections = false;
    [Tooltip("Сила отражений на материалах (если включены выше).")]
    [Range(0f, 1f)] public float reflectionIntensity = 0.05f;

    [Header("НАСТОЯЩИЙ ТУМАН СЦЕНЫ (не туман войны, обычный Unity-туман)")]
    [Tooltip("Включён ли туман: дальние объекты растворяются в цвете тумана. Даёт глубину кадру.")]
    public bool sceneFogEnabled = true;
    [Tooltip("Тип тумана. Exponential — плавное уплотнение с дистанцией (стандарт для мглы).")]
    public FogMode sceneFogMode = FogMode.Exponential;
    [Tooltip("Цвет тумана. Почти чёрный = даль исчезает в темноту; серый = молочная мгла.")]
    public Color sceneFogColor = new Color(0.006f, 0.007f, 0.010f, 1f);
    [Tooltip("Плотность тумана. 0.018 — лёгкая мгла; 0.05+ = густой туман в паре метров.")]
    [Range(0f, 0.2f)] public float sceneFogDensity = 0.018f;

    [Header("PS1-ФИЛЬТР (пикселизация на всё изображение)")]
    [Tooltip("Включён ли PS1-фильтр. В игре фильтр также переключается стрелкой ВЛЕВО.")]
    public bool ps1FilterEnabled = true;
    [Tooltip("Вертикальное разрешение внутреннего буфера. 240 = настоящая PS1; больше = мельче и чётче пиксели.")]
    [Range(72, 1080)] public int ps1PixelHeight = 240;
    [Tooltip("Число уровней цвета: 2^значения на канал. 4.8 = жирные ступени цвета (ретро); 6–7 = спокойнее, меньше цветных крапинок.")]
    [Range(1f, 8f)] public float ps1ColorLevels = 4.8f;
    [Tooltip("Сила Bayer-дизеринга (шахматный шум на переходах цвета). Уменьшай, если «глаза выедает». 0 = чистые ступени без шума.")]
    [Range(0f, 1f)] public float ps1Dither = 0.20f;
    [Tooltip("Сила сканлайнов (горизонтальные полосы как у ЭЛТ). 0.04–0.06 = лёгкая текстура экрана.")]
    [Range(0f, 1f)] public float ps1Scanlines = 0.045f;
    [Tooltip("Случайное зерно на пиксель (живой шум изображения). Больше = грязнее/дрожит картинка.")]
    [Range(0f, 1f)] public float ps1Noise = 0.018f;
    [Tooltip("Сколько шума/дизеринга остаётся в ТЕМНЫХ участках. 0 = чёрный абсолютно чистый; 0.12 = лёгкое шипение в тенях.")]
    [Range(0f, 1f)] public float ps1DarkNoiseKeep = 0.12f;
    [Tooltip("Порог яркости, ниже которого участок считается «тёмным» (влияет на Dark Noise Keep).")]
    [Range(0f, 0.5f)] public float ps1DarkNoiseLuminance = 0.22f;

    [Header("ЦВЕТОКОРРЕКЦИЯ")]
    [Tooltip("Применять настройки цвета ниже к Volume.")]
    public bool colorGradingEnabled = true;
    [Tooltip("Экспозиция после тонмаппинга (в стопах). Меньше (в минус) = темнее вся картинка.")]
    [Range(-5f, 5f)] public float postExposure = -0.25f;
    [Tooltip("Контраст. +22 при ночной сцене выжигает переходы и раздувает насыщенность — держи 0–8.")]
    [Range(-100f, 100f)] public float contrast = 22f;
    [Tooltip("Насыщенность. В минус = цвет приглушается; -30 убирает «пламенно-красные» стены.")]
    [Range(-100f, 100f)] public float saturation = -18f;
    [Tooltip("Цветовой фильтр поверх всего (умножение). Чуть синеватый даёт холодный ночной оттенок.")]
    public Color colorFilter = new Color(0.92f, 0.94f, 1f, 1f);
    [Tooltip("Тонмаппинг: сжимает яркие значения в приятный диапазон. Держать включённым, иначе белое рвётся в клип.")]
    public bool tonemappingEnabled = true;
    [Tooltip("Режим тонмаппинга. Neutral — честный, не перекрашивает; ACES — «киношной», контрастнее и теплее.")]
    public TonemappingMode tonemappingMode = TonemappingMode.Neutral;

    [Header("BLOOM / СВЕЧЕНИЕ")]
    [Tooltip("Включён ли bloom (свечение вокруг ярких мест).")]
    public bool bloomEnabled = true;
    [Tooltip("Сила свечения. 0.35 при ярком пятне фонаря даёт ореол — снижай до 0.1, если выбеливает.")]
    [Range(0f, 10f)] public float bloomIntensity = 0.35f;
    [Tooltip("Порог яркости, с которого начинается свечение. Больше = светится только реально яркое.")]
    [Range(0f, 5f)] public float bloomThreshold = 1.15f;
    [Tooltip("Разброс свечения: 0 = жёсткий тонкий ореол, 1 = размазанное сияние.")]
    [Range(0f, 1f)] public float bloomScatter = 0.55f;
    [Tooltip("Оттенок свечения (обычно белый).")]
    public Color bloomTint = Color.white;

    [Header("ВИНЬЕТКА / ЛИНЗЫ")]
    [Tooltip("Включена ли виньетка (затемнение по краям кадра).")]
    public bool vignetteEnabled = true;
    [Tooltip("Сила затемнения краёв. Больше = туннельный эффект.")]
    [Range(0f, 1f)] public float vignetteIntensity = 0.38f;
    [Tooltip("Мягкость перехода виньетки: меньше = резкая граница, больше = плавное затухание.")]
    [Range(0.01f, 1f)] public float vignetteSmoothness = 0.42f;
    [Tooltip("Ниже 0.5 — виньетка по форме экрана, 0.5+ — идеально круглая.")]
    [Range(0f, 1f)] public float vignetteRoundness = 0.75f;
    [Tooltip("Цвет виньетки (обычно чёрный).")]
    public Color vignetteColor = Color.black;
    [Tooltip("Хроматическая аберрация: цветные ореолы по краям кадра, как у дешёвой оптики.")]
    public bool chromaticAberrationEnabled = true;
    [Tooltip("Сила хроматической аберрации. 0.08 = едва заметная по углам.")]
    [Range(0f, 1f)] public float chromaticAberrationIntensity = 0.08f;
    [Tooltip("Искажение линзы (выпуклость/вогнутость кадра). Обычно выключено.")]
    public bool lensDistortionEnabled = false;
    [Tooltip("Сила искажения: минус = вогнутость (рыбий глаз внутрь), плюс = выпуклость наружу.")]
    [Range(-1f, 1f)] public float lensDistortionIntensity = 0.08f;
    [Tooltip("Масштаб картинки при искажении линзы.")]
    [Range(0.01f, 2f)] public float lensDistortionScale = 1f;

    [Header("ПЛЁНОЧНОЕ ЗЕРНО")]
    [Tooltip("Включено ли плёночное зерно поверх кадра.")]
    public bool filmGrainEnabled = true;
    [Tooltip("Сила зерна. На тёмных сценах смотрится как «шумность картинки» — 0.1 достаточно.")]
    [Range(0f, 1f)] public float filmGrainIntensity = 0.24f;
    [Tooltip("Насколько зерно реагирует на яркость: 0 = одинаково везде, 1 = почти только в средних тонах.")]
    [Range(0f, 1f)] public float filmGrainResponse = 0.8f;

    [Header("КАЧЕСТВО ТЕНЕЙ")]
    [Tooltip("Реалтайм-тени в проекте целиком (главный свет + фонарь, если у него включены тени).")]
    public bool enableRealtimeShadows = true;
    [Tooltip("Разрешение карт теней главного света. Больше = чётче, дороже.")]
    public UnityEngine.ShadowResolution globalShadowResolution = UnityEngine.ShadowResolution.High;
    [Tooltip("Дальность, на которой главный свет ещё рисует тени (метры).")]
    [Range(0f, 200f)] public float globalShadowDistance = 35f;
    [Tooltip("Количество каскадов теней: 0 = одна карта, 2–4 = чёткие тени вблизи и грубые вдали.")]
    [Range(0, 4)] public int shadowCascades = 2;

    bool _initialized;
    float _dofFocusSmooth = -1f;

    /// <summary>Вызывается контроллером игрока, когда все ссылки в сцене собраны.</summary>
    public void Initialize(ProceduralFirstPersonController owner)
    {
        if (owner != null) player = owner;
        ResolveReferences();
        DisableLeftoverFogOfWar();
        if (flashlight != null) flashlight.enabled = flashlightStartsOn;
        ApplyAllSettings();
        _initialized = true;
    }

    void Start()
    {
        if (!_initialized) Initialize(player != null ? player : GetComponent<ProceduralFirstPersonController>());
    }

    void Update()
    {
        if (Application.isPlaying || _initialized) return;
        // Редакторский предпросмотр: применяем ночь/фонарь/пост прямо в
        // edit-mode, чтобы Game-вид выглядел как игра. Фонарь не трогаем —
        // включается галкой на компоненте First Person Flashlight.
        ResolveReferences();
        DisableLeftoverFogOfWar();
        ApplyAllSettings();
        _initialized = true;
    }

    void OnValidate()
    {
        // Правки в инспекторе применяются сразу: и в Play, и в редакторе.
        if (!_initialized) return;
        ResolveReferences();
        ApplyAllSettings();
    }

    void LateUpdate()
    {
        if (flashlight != null && playerCamera != null)
        {
            Vector3 worldOffset = playerCamera.transform.TransformVector(flashlightLocalOffset);
            Quaternion rotation = playerCamera.transform.rotation * Quaternion.Euler(flashlightRotationOffset);
            flashlight.transform.SetPositionAndRotation(playerCamera.transform.position + worldOffset, rotation);
        }
        UpdateDoFAutoFocus();
    }

    /// <summary>
    /// Автофокус DoF: дистанция фокуса подтягивается к точке под прицелом
    /// (рейкаст из центра экрана). Стена, на которую смотришь, становится резкой.
    /// </summary>
    void UpdateDoFAutoFocus()
    {
        if (!dofEnabled || !dofAutoFocus || playerCamera == null) return;

        float target = dofFocusDistance;
        if (Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward,
                out RaycastHit hit, 30f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            target = Mathf.Max(0.4f, hit.distance);

        if (_dofFocusSmooth < 0f) _dofFocusSmooth = target;
        _dofFocusSmooth = Mathf.Lerp(_dofFocusSmooth, target, 1f - Mathf.Exp(-dofAutoFocusSpeed * Time.deltaTime));

        if (postProcessVolume != null && postProcessVolume.profile != null &&
            postProcessVolume.profile.TryGet(out DepthOfField dof) && dof.active)
            dof.focusDistance.value = _dofFocusSmooth;
    }

    void ResolveReferences()
    {
        if (player == null) player = GetComponent<ProceduralFirstPersonController>();
        if (playerCamera == null && player != null) playerCamera = player.playerCamera;
        if (playerCamera == null) playerCamera = Camera.main;
        if (flashlight == null && player != null) flashlight = player.flashlight;
        if (flashlight == null && playerCamera != null) flashlight = playerCamera.GetComponentInChildren<Light>();
        if (ps1Filter == null && playerCamera != null) ps1Filter = playerCamera.GetComponent<Ps1Filter>();
        if (postProcessVolume == null) postProcessVolume = FindFirstObjectByType<Volume>();
    }

    /// <summary>
    /// Туман войны выведен из FPS-сцены. Если компонент ещё висит в сцене,
    /// он снова затемнил бы весь экран — поэтому гасим любой остаток на старте.
    /// </summary>
    void DisableLeftoverFogOfWar()
    {
        foreach (FogOfWarController fog in FindObjectsByType<FogOfWarController>(FindObjectsSortMode.None))
        {
            if (fog != null && fog.enabled)
            {
                fog.enabled = false;
                Debug.Log("[ColdCar] Остаточный FogOfWarController выключен (туман войны выпилен из FPS-сцены).", fog);
            }
        }
    }

    public void ApplyAllSettings()
    {
        ResolveReferences();
        ApplySmoothnessSettings();
        ApplyCameraSettings();
        ApplyFlashlightProperties();
        ApplyWorldLighting();
        ApplySceneFog();
        ApplyPostProcessing();
        ApplyShadowQuality();
    }

    void ApplySmoothnessSettings()
    {
        // VSync + лимит FPS — фундамент плавности: без них картинка рвётся
        // и дёргается независимо от всех остальных настроек.
        QualitySettings.vSyncCount = vsyncCount;
        Application.targetFrameRate = targetFrameRate;
    }

    public void ToggleFlashlight()
    {
        ResolveReferences();
        if (flashlight == null) return;
        flashlight.enabled = !flashlight.enabled;
    }

    void ApplyCameraSettings()
    {
        if (!overrideCameraSettings || playerCamera == null) return;
        playerCamera.fieldOfView = cameraFieldOfView;
        playerCamera.nearClipPlane = cameraNearClip;
        playerCamera.farClipPlane = cameraFarClip;
        playerCamera.backgroundColor = cameraBackgroundColor;
        playerCamera.allowHDR = cameraHDR;

        UniversalAdditionalCameraData cameraData = playerCamera.GetUniversalAdditionalCameraData();
        // HDR-вывод на SDR-монитор = пересветы и усталость глаз; по умолчанию off.
        cameraData.allowHDROutput = allowHdROutput;
        // AA применяется ДО пикселизации PS1 — по желанию смягчает «звон» ярких краёв.
        cameraData.antialiasing = antialiasingMode;
        cameraData.antialiasingQuality = antialiasingQuality;
    }

    void ApplyFlashlightProperties()
    {
        if (flashlight == null) return;
        flashlight.type = LightType.Spot;
        // Force per-pixel: спот не должен деградировать до вершинного света
        // на огромных квадах пола/стен.
        flashlight.renderMode = LightRenderMode.ForcePixel;
        flashlight.color = flashlightColor;
        flashlight.intensity = flashlightIntensity;
        flashlight.range = flashlightRange;
        flashlight.spotAngle = flashlightSpotAngle;
        flashlight.innerSpotAngle = Mathf.Min(flashlightInnerSpotAngle, flashlightSpotAngle);
        flashlight.shadows = flashlightCastsShadows && enableRealtimeShadows ? LightShadows.Soft : LightShadows.None;
        flashlight.shadowStrength = flashlightShadowStrength;
        flashlight.shadowBias = flashlightShadowBias;
        flashlight.shadowNormalBias = flashlightShadowNormalBias;
        flashlight.shadowNearPlane = flashlightShadowNearPlane;
    }

    /// <summary>Публично: менеджер комнат восстанавливает ночную атмосферу при входе в обычную комнату.</summary>
    public void ApplyWorldLighting()
    {
        if (overrideAmbientLighting)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambientColor * ambientIntensity;
        }
        // Стандартный процедурный скайбокс яркий сам по себе и не зависит от
        // нашей ночной освещённости — на открытых комнатах он выглядел как
        // гигантский белый свет. Ночью небо чёрное, а в дневных комнатах
        // (крыша/открытые пространства) атмосферой управляет RoomManager.
        if (blackNightSky && !ProceduralRoomManager.OutdoorRoomActive && RenderSettings.skybox != null)
            RenderSettings.skybox = null;
        RenderSettings.ambientIntensity = ambientIntensity;
        RenderSettings.defaultReflectionMode = useEnvironmentReflections ? DefaultReflectionMode.Skybox : DefaultReflectionMode.Custom;
        RenderSettings.reflectionIntensity = useEnvironmentReflections ? reflectionIntensity : 0f;

        if (overrideDirectionalLights)
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light light in lights)
            {
                if (light != null && light.type == LightType.Directional)
                {
                    light.color = directionalLightColor;
                    light.intensity = directionalLightIntensity;
                    light.shadows = enableRealtimeShadows ? LightShadows.Soft : LightShadows.None;
                }
            }
        }
    }

    public void ApplySceneFog()
    {
        RenderSettings.fog = sceneFogEnabled;
        RenderSettings.fogMode = sceneFogMode;
        RenderSettings.fogDensity = sceneFogDensity;
        RenderSettings.fogColor = sceneFogColor;
    }

    void ApplyPostProcessing()
    {
        if (ps1Filter != null)
        {
            ps1Filter.filterEnabled = ps1FilterEnabled;
            ps1Filter.pixelHeight = ps1PixelHeight;
            ps1Filter.quantizeLevels = ps1ColorLevels;
            ps1Filter.dither = ps1Dither;
            ps1Filter.scanlines = ps1Scanlines;
            ps1Filter.noise = ps1Noise;
            ps1Filter.darkNoiseKeep = ps1DarkNoiseKeep;
            ps1Filter.darkNoiseLuminance = ps1DarkNoiseLuminance;
            ps1Filter.edgeSmoothing = ps1EdgeSmoothing;
        }

        if (postProcessVolume == null) return;
        VolumeProfile profile = postProcessVolume.profile;
        if (profile == null) return;

        ColorAdjustments color;
        if (!profile.TryGet(out color)) color = profile.Add<ColorAdjustments>(true);
        color.active = colorGradingEnabled;
        Set(color.postExposure, postExposure);
        Set(color.contrast, contrast);
        Set(color.saturation, saturation);
        Set(color.colorFilter, colorFilter);

        Tonemapping tone;
        if (!profile.TryGet(out tone)) tone = profile.Add<Tonemapping>(true);
        tone.active = tonemappingEnabled;
        Set(tone.mode, tonemappingMode);

        Bloom bloom;
        if (!profile.TryGet(out bloom)) bloom = profile.Add<Bloom>(true);
        bloom.active = bloomEnabled;
        Set(bloom.intensity, bloomIntensity);
        Set(bloom.threshold, bloomThreshold);
        Set(bloom.scatter, bloomScatter);
        Set(bloom.tint, bloomTint);

        Vignette vignette;
        if (!profile.TryGet(out vignette)) vignette = profile.Add<Vignette>(true);
        vignette.active = vignetteEnabled;
        Set(vignette.intensity, vignetteIntensity);
        Set(vignette.smoothness, vignetteSmoothness);
        Set(vignette.rounded, vignetteRoundness >= 0.5f);
        Set(vignette.color, vignetteColor);

        ChromaticAberration chromatic;
        if (!profile.TryGet(out chromatic)) chromatic = profile.Add<ChromaticAberration>(true);
        chromatic.active = chromaticAberrationEnabled;
        Set(chromatic.intensity, chromaticAberrationIntensity);

        LensDistortion lens;
        if (!profile.TryGet(out lens)) lens = profile.Add<LensDistortion>(true);
        lens.active = lensDistortionEnabled;
        Set(lens.intensity, lensDistortionIntensity);
        Set(lens.scale, lensDistortionScale);

        FilmGrain grain;
        if (!profile.TryGet(out grain)) grain = profile.Add<FilmGrain>(true);
        grain.active = filmGrainEnabled;
        Set(grain.intensity, filmGrainIntensity);
        Set(grain.response, filmGrainResponse);

        MotionBlur blur;
        if (!profile.TryGet(out blur)) blur = profile.Add<MotionBlur>(true);
        blur.active = motionBlurEnabled;
        Set(blur.mode, MotionBlurMode.CameraOnly);
        Set(blur.intensity, motionBlurIntensity);
        Set(blur.clamp, motionBlurClamp);

        DepthOfField dof;
        if (!profile.TryGet(out dof)) dof = profile.Add<DepthOfField>(true);
        dof.active = dofEnabled;
        Set(dof.mode, dofMode);
        float focus = _dofFocusSmooth > 0f ? _dofFocusSmooth : dofFocusDistance;
        Set(dof.focusDistance, focus);
        Set(dof.aperture, dofAperture);
        Set(dof.focalLength, dofFocalLength);
        // Для Gaussian-режима границы размытия выводятся от дистанции фокуса.
        Set(dof.gaussianStart, focus * 0.7f);
        Set(dof.gaussianEnd, focus * 1.8f);
        Set(dof.gaussianMaxRadius, 1f);
    }

    void ApplyShadowQuality()
    {
        QualitySettings.shadows = enableRealtimeShadows ? UnityEngine.ShadowQuality.All : UnityEngine.ShadowQuality.Disable;
        QualitySettings.shadowResolution = globalShadowResolution;
        QualitySettings.shadowDistance = globalShadowDistance;
        QualitySettings.shadowCascades = shadowCascades;
    }

    static void Set<T>(VolumeParameter<T> parameter, T value)
    {
        if (parameter == null) return;
        parameter.overrideState = true;
        parameter.value = value;
    }
}
