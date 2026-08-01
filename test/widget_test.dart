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
}
