// [담당업무 1] 동시 업로드 수 제한용 세마포어.

import 'dart:async';
import 'dart:collection';

/// 동시 실행 수를 제한하는 세마포어.
///
/// ```dart
/// final sem = Semaphore(6); // 최대 6개 동시 실행
/// await sem.run(() => dio.get(url));
/// ```
class Semaphore {
  Semaphore(this.maxCount);

  final int maxCount;
  int _current = 0;
  final _queue = Queue<Completer<void>>();

  Future<T> run<T>(Future<T> Function() task) async {
    if (_current >= maxCount) {
      final completer = Completer<void>();
      _queue.add(completer);
      await completer.future;
    }
    _current++;
    try {
      return await task();
    } finally {
      _current--;
      if (_queue.isNotEmpty) {
        _queue.removeFirst().complete();
      }
    }
  }
}
