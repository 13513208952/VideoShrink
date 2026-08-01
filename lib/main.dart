import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
  SystemChrome.setSystemUIOverlayStyle(
    const SystemUiOverlayStyle(
      statusBarColor: Colors.transparent,
      systemNavigationBarColor: Colors.transparent,
      systemNavigationBarDividerColor: Colors.transparent,
      systemStatusBarContrastEnforced: false,
      systemNavigationBarContrastEnforced: false,
    ),
  );
  runApp(const VideoShrinkApp());
}

class VideoShrinkApp extends StatelessWidget {
  const VideoShrinkApp({super.key});

  @override
  Widget build(BuildContext context) {
    const seed = Color(0xFF00A779);
    return MaterialApp(
      title: 'VideoShrink',
      debugShowCheckedModeBanner: false,
      themeMode: ThemeMode.system,
      theme: _theme(Brightness.light, seed),
      darkTheme: _theme(Brightness.dark, seed),
      home: const HomePage(),
    );
  }

  ThemeData _theme(Brightness brightness, Color seed) {
    final colors = ColorScheme.fromSeed(
      seedColor: seed,
      brightness: brightness,
      surface: brightness == Brightness.light
          ? const Color(0xFFF5FAF7)
          : const Color(0xFF0D1512),
    );
    return ThemeData(
      useMaterial3: true,
      colorScheme: colors,
      scaffoldBackgroundColor: colors.surface,
      fontFamily: 'sans',
      cardTheme: CardThemeData(
        elevation: 0,
        color: colors.surfaceContainerLow,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(24)),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(56),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(18),
          ),
          textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700),
        ),
      ),
    );
  }
}

enum EncoderChoice { h264, hevc, av1 }

enum DecoderChoice { auto, hardware, software }

enum SaveMode { newFolder, overwrite }

class HomePage extends StatefulWidget {
  const HomePage({super.key});

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> with WidgetsBindingObserver {
  static const _platform = MethodChannel('com.videoshrink.android/platform');

  EncoderChoice encoder = EncoderChoice.av1;
  DecoderChoice decoder = DecoderChoice.auto;
  SaveMode saveMode = SaveMode.newFolder;
  double targetRatio = 0.15;
  String resolution = '1080p';
  int av1Preset = 2;
  int tab = 0;
  bool hasAllFilesAccess = false;
  bool serviceRunning = false;
  bool busy = false;
  bool h264Hardware = false;
  bool hevcHardware = false;
  bool av1Hardware = false;
  String? sourceUri;
  String? sourceName;
  String? outputUri;
  String? outputName;
  String taskStatus = 'idle';
  String? taskCurrent;
  String? taskError;
  String? taskWarning;
  int taskTotal = 0;
  int taskCompleted = 0;
  int taskProgress = 0;
  Timer? pollTimer;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _refreshPlatformState();
  }

  @override
  void dispose() {
    pollTimer?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _refreshPlatformState();
  }

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final dark = Theme.of(context).brightness == Brightness.dark;
    final wide = MediaQuery.sizeOf(context).width >= 720;
    final pages = IndexedStack(
      index: tab,
      children: [
        _buildCompressPage(wide: wide),
        _buildTaskPage(wide: wide),
        const _EmptyPage(
          icon: Icons.tune_rounded,
          title: '偏好设置',
          detail: '高级编码参数、扫描规则和安全校验将在这里配置。',
        ),
      ],
    );
    final overlay = SystemUiOverlayStyle(
      statusBarColor: Colors.transparent,
      statusBarIconBrightness: dark ? Brightness.light : Brightness.dark,
      statusBarBrightness: dark ? Brightness.dark : Brightness.light,
      systemNavigationBarColor: Colors.transparent,
      systemNavigationBarDividerColor: Colors.transparent,
      systemNavigationBarIconBrightness: dark
          ? Brightness.light
          : Brightness.dark,
      systemStatusBarContrastEnforced: false,
      systemNavigationBarContrastEnforced: false,
    );
    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: overlay,
      child: Scaffold(
        extendBody: !wide,
        body: wide
            ? Row(
                children: [
                  SafeArea(
                    right: false,
                    child: NavigationRail(
                      selectedIndex: tab,
                      labelType: NavigationRailLabelType.all,
                      onDestinationSelected: (value) =>
                          setState(() => tab = value),
                      destinations: const [
                        NavigationRailDestination(
                          icon: Icon(Icons.compress_rounded),
                          label: Text('压缩'),
                        ),
                        NavigationRailDestination(
                          icon: Icon(Icons.receipt_long_rounded),
                          label: Text('任务'),
                        ),
                        NavigationRailDestination(
                          icon: Icon(Icons.settings_rounded),
                          label: Text('设置'),
                        ),
                      ],
                    ),
                  ),
                  VerticalDivider(
                    width: 1,
                    thickness: 1,
                    color: colors.outlineVariant,
                  ),
                  Expanded(child: pages),
                ],
              )
            : pages,
        bottomNavigationBar: wide
            ? null
            : NavigationBar(
                backgroundColor: colors.surfaceContainer.withValues(
                  alpha: 0.94,
                ),
                selectedIndex: tab,
                onDestinationSelected: (value) => setState(() => tab = value),
                destinations: const [
                  NavigationDestination(
                    icon: Icon(Icons.compress_rounded),
                    label: '压缩',
                  ),
                  NavigationDestination(
                    icon: Icon(Icons.receipt_long_rounded),
                    label: '任务',
                  ),
                  NavigationDestination(
                    icon: Icon(Icons.settings_rounded),
                    label: '设置',
                  ),
                ],
              ),
      ),
    );
  }

  EdgeInsets _pageInsets({required bool wide, double top = 12}) {
    final media = MediaQuery.of(context);
    final safeWidth = media.size.width - media.viewPadding.horizontal;
    final candidateSide = (safeWidth - 760) / 2;
    final centeredSide = candidateSide > 20 ? candidateSide : 20.0;
    return EdgeInsets.fromLTRB(
      media.viewPadding.left + centeredSide,
      media.viewPadding.top + top,
      media.viewPadding.right + centeredSide,
      media.viewPadding.bottom + (wide ? 36 : 132),
    );
  }

  Widget _buildCompressPage({required bool wide}) {
    return CustomScrollView(
      physics: const BouncingScrollPhysics(),
      slivers: [
        SliverPadding(
          padding: _pageInsets(wide: wide),
          sliver: SliverList.list(
            children: [
              const _Header(),
              const SizedBox(height: 20),
              _AccessBanner(
                granted: hasAllFilesAccess,
                onTap: _requestAllFilesAccess,
              ),
              const _SectionTitle('文件'),
              _FolderCard(
                icon: Icons.video_library_rounded,
                title: sourceName ?? '选择源文件夹',
                subtitle: sourceUri == null ? '扫描其中符合条件的视频' : '已获得持久读取权限',
                onTap: () => _pickDirectory('source'),
              ),
              const SizedBox(height: 12),
              _FolderCard(
                icon: Icons.folder_open_rounded,
                title: outputName ?? '输出位置',
                subtitle: outputUri != null
                    ? '已获得持久写入权限'
                    : saveMode == SaveMode.newFolder
                    ? '默认在源目录创建 VideoShrink 输出文件夹'
                    : '覆盖源文件 · 请确保已有备份',
                warning: saveMode == SaveMode.overwrite,
                onTap: () => _pickDirectory('output'),
              ),
              const SizedBox(height: 14),
              SegmentedButton<SaveMode>(
                segments: const [
                  ButtonSegment(
                    value: SaveMode.newFolder,
                    icon: Icon(Icons.create_new_folder_rounded),
                    label: Text('新建文件夹'),
                  ),
                  ButtonSegment(
                    value: SaveMode.overwrite,
                    icon: Icon(Icons.warning_amber_rounded),
                    label: Text('覆盖原文件'),
                  ),
                ],
                selected: {saveMode},
                showSelectedIcon: false,
                onSelectionChanged: (value) =>
                    setState(() => saveMode = value.first),
              ),
              const _SectionTitle('编码器'),
              Row(
                children: [
                  Expanded(
                    child: _EncoderCard(
                      title: 'H.264',
                      badge: '最快',
                      icon: Icons.speed_rounded,
                      selected: encoder == EncoderChoice.h264,
                      onTap: () => setState(() => encoder = EncoderChoice.h264),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: _EncoderCard(
                      title: 'HEVC',
                      badge: '均衡',
                      icon: Icons.balance_rounded,
                      selected: encoder == EncoderChoice.hevc,
                      onTap: () => setState(() => encoder = EncoderChoice.hevc),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: _EncoderCard(
                      title: 'AV1',
                      badge: '最省空间',
                      icon: Icons.auto_awesome_rounded,
                      selected: encoder == EncoderChoice.av1,
                      onTap: () => setState(() => encoder = EncoderChoice.av1),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 14),
              _PerformanceHint(
                encoder: encoder,
                hardware: switch (encoder) {
                  EncoderChoice.h264 => h264Hardware,
                  EncoderChoice.hevc => hevcHardware,
                  EncoderChoice.av1 => av1Hardware,
                },
              ),
              const SizedBox(height: 14),
              _SurfaceCard(
                child: Row(
                  children: [
                    const Icon(Icons.memory_rounded),
                    const SizedBox(width: 12),
                    const Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            '解码方式',
                            style: TextStyle(fontWeight: FontWeight.w700),
                          ),
                          Text(
                            '强制模式失败时不会偷偷切换',
                            style: TextStyle(fontSize: 11, color: Colors.grey),
                          ),
                        ],
                      ),
                    ),
                    DropdownButton<DecoderChoice>(
                      value: decoder,
                      underline: const SizedBox.shrink(),
                      items: const [
                        DropdownMenuItem(
                          value: DecoderChoice.auto,
                          child: Text('自动'),
                        ),
                        DropdownMenuItem(
                          value: DecoderChoice.hardware,
                          child: Text('硬件'),
                        ),
                        DropdownMenuItem(
                          value: DecoderChoice.software,
                          child: Text('软件'),
                        ),
                      ],
                      onChanged: (value) => setState(() => decoder = value!),
                    ),
                  ],
                ),
              ),
              if (encoder == EncoderChoice.av1) ...[
                const SizedBox(height: 14),
                _SurfaceCard(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          const Expanded(
                            child: Text(
                              'SVT-AV1 preset',
                              style: TextStyle(fontWeight: FontWeight.w700),
                            ),
                          ),
                          Text(
                            '$av1Preset',
                            style: TextStyle(
                              color: Theme.of(context).colorScheme.primary,
                              fontSize: 22,
                              fontWeight: FontWeight.w800,
                            ),
                          ),
                        ],
                      ),
                      Slider(
                        value: av1Preset.toDouble(),
                        min: 0,
                        max: 13,
                        divisions: 13,
                        label: '$av1Preset',
                        onChanged: (value) =>
                            setState(() => av1Preset = value.round()),
                      ),
                      Text(
                        av1Preset >= 12
                            ? '0 最慢、压缩效率最高；13 最快。M12/M13 使用 SVT 对应的实时预测结构，不会映射为 M11。'
                            : '0 最慢、压缩效率最高；13 最快。所选值不会被自动修改。',
                        style: const TextStyle(
                          fontSize: 12,
                          color: Colors.grey,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
              const _SectionTitle('输出目标'),
              _SurfaceCard(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        const Expanded(
                          child: Text(
                            '目标体积',
                            style: TextStyle(
                              fontSize: 17,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                        Text(
                          '${(targetRatio * 100).round()}%',
                          style: TextStyle(
                            color: Theme.of(context).colorScheme.primary,
                            fontSize: 24,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                      ],
                    ),
                    const Text(
                      '相对于原文件大小',
                      style: TextStyle(color: Colors.grey),
                    ),
                    Slider(
                      value: targetRatio,
                      min: 0.01,
                      max: 1.00,
                      divisions: 99,
                      label: '${(targetRatio * 100).round()}%',
                      onChanged: (value) => setState(() => targetRatio = value),
                    ),
                    const Divider(height: 26),
                    Row(
                      children: [
                        const Icon(Icons.aspect_ratio_rounded),
                        const SizedBox(width: 12),
                        const Expanded(
                          child: Text(
                            '输出分辨率',
                            style: TextStyle(fontWeight: FontWeight.w600),
                          ),
                        ),
                        DropdownButton<String>(
                          value: resolution,
                          underline: const SizedBox.shrink(),
                          items:
                              const [
                                    '原始',
                                    '4320p',
                                    '2880p',
                                    '2160p',
                                    '1440p',
                                    '1080p',
                                    '720p',
                                    '480p',
                                  ]
                                  .map(
                                    (item) => DropdownMenuItem(
                                      value: item,
                                      child: Text(item),
                                    ),
                                  )
                                  .toList(),
                          onChanged: (value) =>
                              setState(() => resolution = value!),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 20),
              FilledButton.icon(
                onPressed: busy
                    ? null
                    : (serviceRunning ? _stopService : _startService),
                icon: busy
                    ? const SizedBox.square(
                        dimension: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Icon(
                        serviceRunning
                            ? Icons.stop_rounded
                            : Icons.play_arrow_rounded,
                      ),
                label: Text(serviceRunning ? '停止后台任务' : '扫描并开始'),
              ),
              const SizedBox(height: 12),
              const Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Icon(
                    Icons.lock_outline_rounded,
                    size: 15,
                    color: Colors.grey,
                  ),
                  SizedBox(width: 6),
                  Flexible(
                    child: Text(
                      '不需要 Root · 锁屏后继续运行 · 原文件默认不改动',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 12, color: Colors.grey),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildTaskPage({required bool wide}) {
    final active =
        serviceRunning ||
        const {
          'scanning',
          'queued',
          'preparing',
          'encoding',
          'writing',
        }.contains(taskStatus);
    final statusText = switch (taskStatus) {
      'scanning' => '正在扫描视频',
      'queued' => '任务已排队',
      'preparing' => '正在复制并准备源视频',
      'encoding' => '正在压缩',
      'writing' => '正在校验并写入',
      'completed' => '全部完成',
      'completed_with_errors' => '处理完成，部分任务失败',
      'error' => '任务出错',
      _ => '暂无任务',
    };
    return ListView(
      padding: _pageInsets(wide: wide, top: 28),
      children: [
        Text(
          '任务队列',
          style: Theme.of(
            context,
          ).textTheme.headlineMedium?.copyWith(fontWeight: FontWeight.w800),
        ),
        const SizedBox(height: 18),
        _SurfaceCard(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(
                    active ? Icons.sync_rounded : Icons.task_alt_rounded,
                    color: Theme.of(context).colorScheme.primary,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      statusText,
                      style: const TextStyle(
                        fontSize: 18,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  if (taskTotal > 0) Text('$taskCompleted / $taskTotal'),
                ],
              ),
              if (taskCurrent != null) ...[
                const SizedBox(height: 14),
                Text(
                  taskCurrent!,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
              const SizedBox(height: 16),
              LinearProgressIndicator(
                value: taskStatus == 'encoding'
                    ? taskProgress / 100
                    : active
                    ? null
                    : taskStatus.startsWith('completed')
                    ? 1
                    : 0,
                minHeight: 9,
                borderRadius: BorderRadius.circular(20),
              ),
              if (taskError?.isNotEmpty == true) ...[
                const SizedBox(height: 14),
                Text(
                  taskError!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              if (taskWarning?.isNotEmpty == true) ...[
                const SizedBox(height: 14),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Icon(
                      Icons.info_outline_rounded,
                      size: 18,
                      color: Colors.amber,
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        taskWarning!,
                        style: const TextStyle(color: Colors.amber),
                      ),
                    ),
                  ],
                ),
              ],
            ],
          ),
        ),
        if (serviceRunning) ...[
          const SizedBox(height: 18),
          OutlinedButton.icon(
            onPressed: busy ? null : _stopService,
            icon: const Icon(Icons.stop_rounded),
            label: const Text('停止当前批次'),
          ),
        ],
      ],
    );
  }

  Future<void> _refreshPlatformState() async {
    try {
      final state = await _platform.invokeMapMethod<String, dynamic>(
        'getState',
      );
      if (!mounted || state == null) return;
      bool hasHardwareEncoder(String key) {
        final encoders = state['encoders'];
        if (encoders is! Map) return false;
        final choices = encoders[key];
        if (choices is! List) return false;
        return choices.any(
          (choice) => choice is Map && choice['hardware'] == true,
        );
      }

      setState(() {
        hasAllFilesAccess = state['hasAllFilesAccess'] == true;
        serviceRunning = state['serviceRunning'] == true;
        sourceUri = state['sourceUri'] as String?;
        sourceName = state['sourceName'] as String?;
        outputUri = state['outputUri'] as String?;
        outputName = state['outputName'] as String?;
        h264Hardware = hasHardwareEncoder('h264');
        hevcHardware = hasHardwareEncoder('hevc');
        av1Hardware = hasHardwareEncoder('av1');
        taskStatus = state['taskStatus'] as String? ?? 'idle';
        taskCurrent = state['taskCurrent'] as String?;
        taskError = state['taskError'] as String?;
        taskWarning = state['taskWarning'] as String?;
        taskTotal = state['taskTotal'] as int? ?? 0;
        taskCompleted = state['taskCompleted'] as int? ?? 0;
        taskProgress = state['taskProgress'] as int? ?? 0;
      });
      pollTimer?.cancel();
      if (serviceRunning) {
        pollTimer = Timer(const Duration(seconds: 1), _refreshPlatformState);
      }
    } on PlatformException catch (error) {
      _message(error.message ?? '无法读取系统状态');
    }
  }

  Future<void> _requestAllFilesAccess() async {
    if (hasAllFilesAccess) {
      _message('已获得全部文件访问权限');
      return;
    }
    try {
      await _platform.invokeMethod<void>('requestAllFilesAccess');
    } on PlatformException catch (error) {
      _message(error.message ?? '无法打开权限设置');
    }
  }

  Future<void> _pickDirectory(String kind) async {
    try {
      final picked = await _platform.invokeMapMethod<String, dynamic>(
        'pickDirectory',
        {'kind': kind},
      );
      if (!mounted || picked == null) return;
      setState(() {
        if (kind == 'source') {
          sourceUri = picked['uri'] as String?;
          sourceName = picked['name'] as String?;
        } else {
          outputUri = picked['uri'] as String?;
          outputName = picked['name'] as String?;
        }
      });
    } on PlatformException catch (error) {
      _message(error.message ?? '无法选择目录');
    }
  }

  Future<void> _startService() async {
    if (sourceUri == null) {
      _message('请先选择源文件夹');
      return;
    }
    setState(() => busy = true);
    try {
      await _platform.invokeMethod<void>('startService', {
        'sourceUri': sourceUri,
        'outputUri': outputUri,
        'overwrite': saveMode == SaveMode.overwrite,
        'encoder': encoder.name,
        'decoder': decoder.name,
        'av1Preset': av1Preset,
        'targetRatio': targetRatio,
        'resolution': resolution,
      });
      if (mounted) {
        setState(() {
          serviceRunning = true;
          tab = 1;
        });
        _message('已开始扫描并建立处理队列');
        pollTimer?.cancel();
        pollTimer = Timer(
          const Duration(milliseconds: 500),
          _refreshPlatformState,
        );
      }
    } on PlatformException catch (error) {
      _message(error.message ?? '无法启动后台服务');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<void> _stopService() async {
    setState(() => busy = true);
    try {
      await _platform.invokeMethod<void>('stopService');
      if (mounted) {
        setState(() => serviceRunning = false);
        _message('后台处理服务已停止');
      }
    } on PlatformException catch (error) {
      _message(error.message ?? '无法停止后台服务');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  void _message(String text) =>
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));
}

class _Header extends StatelessWidget {
  const _Header();

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Container(
          width: 52,
          height: 52,
          decoration: BoxDecoration(
            gradient: const LinearGradient(
              colors: [Color(0xFF00B983), Color(0xFF007B63)],
              begin: Alignment.topLeft,
              end: Alignment.bottomRight,
            ),
            borderRadius: BorderRadius.circular(17),
            boxShadow: [
              BoxShadow(
                color: const Color(0xFF00A779).withValues(alpha: 0.24),
                blurRadius: 20,
                offset: const Offset(0, 8),
              ),
            ],
          ),
          child: const Icon(
            Icons.movie_filter_rounded,
            color: Colors.white,
            size: 28,
          ),
        ),
        const SizedBox(width: 14),
        const Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'VideoShrink',
                style: TextStyle(fontSize: 25, fontWeight: FontWeight.w800),
              ),
              Text('把空间留给更重要的内容', style: TextStyle(color: Colors.grey)),
            ],
          ),
        ),
        const IconButton.filledTonal(
          onPressed: null,
          icon: Icon(Icons.bolt_rounded),
          tooltip: '性能状态',
        ),
      ],
    );
  }
}

class _AccessBanner extends StatelessWidget {
  const _AccessBanner({required this.granted, required this.onTap});
  final bool granted;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Material(
      color: granted ? colors.secondaryContainer : colors.primaryContainer,
      borderRadius: BorderRadius.circular(20),
      child: InkWell(
        borderRadius: BorderRadius.circular(20),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Icon(
                granted ? Icons.verified_rounded : Icons.folder_copy_rounded,
                color: colors.onPrimaryContainer,
              ),
              const SizedBox(width: 13),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      granted ? '全部文件访问已授权' : '需要文件访问权限',
                      style: const TextStyle(fontWeight: FontWeight.w700),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      granted ? '可扫描文件系统并写入处理结果' : '用于扫描、写入和安全替换视频',
                      style: const TextStyle(fontSize: 12),
                    ),
                  ],
                ),
              ),
              Icon(
                granted ? Icons.check_rounded : Icons.arrow_forward_ios_rounded,
                size: 16,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(3, 26, 0, 10),
    child: Text(
      text,
      style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w800),
    ),
  );
}

class _FolderCard extends StatelessWidget {
  const _FolderCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
    this.warning = false,
  });
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;
  final bool warning;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return _SurfaceCard(
      padding: EdgeInsets.zero,
      child: InkWell(
        borderRadius: BorderRadius.circular(24),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(17),
          child: Row(
            children: [
              Container(
                width: 45,
                height: 45,
                decoration: BoxDecoration(
                  color: warning
                      ? colors.errorContainer
                      : colors.secondaryContainer,
                  borderRadius: BorderRadius.circular(15),
                ),
                child: Icon(
                  icon,
                  color: warning ? colors.error : colors.secondary,
                ),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      subtitle,
                      style: const TextStyle(fontSize: 12, color: Colors.grey),
                    ),
                  ],
                ),
              ),
              const Icon(Icons.chevron_right_rounded, color: Colors.grey),
            ],
          ),
        ),
      ),
    );
  }
}

class _EncoderCard extends StatelessWidget {
  const _EncoderCard({
    required this.title,
    required this.badge,
    required this.icon,
    required this.selected,
    required this.onTap,
  });
  final String title;
  final String badge;
  final IconData icon;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return AnimatedContainer(
      duration: const Duration(milliseconds: 180),
      decoration: BoxDecoration(
        color: selected ? colors.primaryContainer : colors.surfaceContainerLow,
        borderRadius: BorderRadius.circular(20),
        border: Border.all(
          color: selected ? colors.primary : colors.outlineVariant,
        ),
      ),
      child: InkWell(
        borderRadius: BorderRadius.circular(20),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 15, horizontal: 8),
          child: Column(
            children: [
              Icon(
                icon,
                color: selected ? colors.primary : colors.onSurfaceVariant,
              ),
              const SizedBox(height: 8),
              Text(title, style: const TextStyle(fontWeight: FontWeight.w800)),
              const SizedBox(height: 2),
              Text(
                badge,
                style: const TextStyle(fontSize: 10, color: Colors.grey),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _PerformanceHint extends StatelessWidget {
  const _PerformanceHint({required this.encoder, required this.hardware});
  final EncoderChoice encoder;
  final bool hardware;
  @override
  Widget build(BuildContext context) {
    final message = switch (encoder) {
      EncoderChoice.h264 => hardware ? '已检测到硬件编码，适合快速批量处理' : '未检测到硬件编码，将使用软件路径',
      EncoderChoice.hevc =>
        hardware ? '已检测到硬件编码，兼顾体积、画质与速度' : '未检测到硬件编码，将使用软件路径',
      EncoderChoice.av1 =>
        hardware ? '已检测到 AV1 硬件编码' : '仅检测到软件 AV1；保持所选画质与分辨率，预计耗时较长',
    };
    return Row(
      children: [
        Icon(
          Icons.info_outline_rounded,
          size: 17,
          color: Theme.of(context).colorScheme.primary,
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            message,
            style: const TextStyle(fontSize: 12, color: Colors.grey),
          ),
        ),
      ],
    );
  }
}

class _SurfaceCard extends StatelessWidget {
  const _SurfaceCard({
    required this.child,
    this.padding = const EdgeInsets.all(18),
  });
  final Widget child;
  final EdgeInsets padding;
  @override
  Widget build(BuildContext context) => Card(
    margin: EdgeInsets.zero,
    child: Padding(padding: padding, child: child),
  );
}

class _EmptyPage extends StatelessWidget {
  const _EmptyPage({
    required this.icon,
    required this.title,
    required this.detail,
  });
  final IconData icon;
  final String title;
  final String detail;
  @override
  Widget build(BuildContext context) => SafeArea(
    child: Center(
      child: Padding(
        padding: const EdgeInsets.all(36),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 58, color: Theme.of(context).colorScheme.primary),
            const SizedBox(height: 18),
            Text(
              title,
              style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w800),
            ),
            const SizedBox(height: 8),
            Text(
              detail,
              textAlign: TextAlign.center,
              style: const TextStyle(color: Colors.grey),
            ),
          ],
        ),
      ),
    ),
  );
}
