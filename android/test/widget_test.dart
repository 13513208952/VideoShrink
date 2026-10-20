import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:videoshrink_android/main.dart';

void main() {
  const channel = MethodChannel('com.videoshrink.android/platform');

  Map<String, Object?> state({Map<String, Object?>? task}) => {
    'hasAllFilesAccess': true,
    'serviceRunning': false,
    'taskStatus': task?['status'] ?? 'idle',
    'taskTotal': task?['total'] ?? 0,
    'taskCompleted': task == null ? 0 : 2,
    'taskProgress': 0,
    'encoders': <String, Object?>{},
    'processedHistoryCount': 12,
    'settings': <String, Object?>{
      'skipProcessed': true,
      'skipGenerated': true,
      'strictMatching': true,
      'includeSubdirectories': true,
      'skipHidden': true,
      'keepScreenOn': true,
    },
    'task': task,
  };

  void mockPlatform(Map<String, Object?> value) {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
          if (call.method == 'getState') return value;
          return null;
        });
    addTearDown(
      () => TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, null),
    );
  }

  testWidgets('shows the compression dashboard', (tester) async {
    mockPlatform(state());
    await tester.pumpWidget(const VideoShrinkApp());
    expect(find.text('VideoShrink'), findsOneWidget);
    expect(find.text('选择源文件夹'), findsOneWidget);
    expect(find.text('AV1'), findsOneWidget);
    await tester.drag(find.byType(CustomScrollView), const Offset(0, -600));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'uses a navigation rail and respects cutout insets on wide screens',
    (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = const Size(1200, 800);
      tester.view.viewPadding = const FakeViewPadding(
        left: 48,
        top: 36,
        right: 24,
        bottom: 20,
      );
      tester.view.padding = const FakeViewPadding(
        left: 48,
        top: 36,
        right: 24,
        bottom: 20,
      );
      addTearDown(tester.view.reset);

      await tester.pumpWidget(const VideoShrinkApp());
      await tester.pumpAndSettle();

      expect(find.byType(NavigationRail), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      final titlePosition = tester.getTopLeft(find.text('VideoShrink'));
      expect(titlePosition.dx, greaterThan(48));
      expect(titlePosition.dy, greaterThan(36));
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('shows persistent batch details and recovery actions', (
    tester,
  ) async {
    mockPlatform(
      state(
        task: <String, Object?>{
          'status': 'cancelled',
          'total': 3,
          'completed': 1,
          'failed': 0,
          'skipped': 1,
          'profile': 'AV1 · preset 2 · 1080p · 15%',
          'files': <Map<String, Object?>>[
            {'name': 'done.mp4', 'size': 1048576, 'status': 'completed'},
            {'name': 'known.mp4', 'size': 2048, 'status': 'skipped'},
            {'name': 'next.mp4', 'size': 4096, 'status': 'pending'},
          ],
        },
      ),
    );
    await tester.pumpWidget(const VideoShrinkApp());
    await tester.pumpAndSettle();
    await tester.tap(find.text('任务').last);
    await tester.pumpAndSettle();

    expect(find.text('队列已终止，可继续处理'), findsOneWidget);
    expect(find.text('done.mp4'), findsOneWidget);
    expect(find.text('已按处理记录自动跳过'), findsOneWidget);
    expect(find.text('继续'), findsOneWidget);
    expect(find.text('删除记录'), findsOneWidget);
  });

  testWidgets('settings page exposes scan and history rules', (tester) async {
    mockPlatform(state());
    await tester.pumpWidget(const VideoShrinkApp());
    await tester.pumpAndSettle();
    await tester.tap(find.text('设置').last);
    await tester.pumpAndSettle();

    expect(find.text('自动跳过已处理文件'), findsOneWidget);
    expect(find.text('忽略本应用生成的文件'), findsOneWidget);
    expect(find.text('前台保持屏幕常亮'), findsOneWidget);
    await tester.drag(find.byType(ListView).last, const Offset(0, -500));
    await tester.pumpAndSettle();
    expect(find.text('已保存 12 条识别记录'), findsOneWidget);
    expect(find.textContaining('Root'), findsNothing);
  });
}
