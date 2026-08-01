import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:videoshrink_android/main.dart';

void main() {
  testWidgets('shows the compression dashboard', (tester) async {
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
}
