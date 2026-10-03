import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:provider/provider.dart';

import 'package:mobile/api/api_client.dart';
import 'package:mobile/screens/login_screen.dart';
import 'package:mobile/state/auth_provider.dart';
import 'package:mobile/state/token_store.dart';

/// AUDIT C-05 (refresh on 401) and C-12 (field errors, timeout).
class _MemoryTokenStore implements TokenStore {
  final Map<String, String> values = {};

  @override
  Future<String?> read(String key) async => values[key];

  @override
  Future<void> write(String key, String value) async => values[key] = value;

  @override
  Future<void> delete(String key) async => values.remove(key);
}

http.Response _json(Object body, [int status = 200]) => http.Response(
      jsonEncode(body),
      status,
      headers: {'content-type': 'application/json'},
    );

Map<String, dynamic> _tokens(String access, String refresh) => {
      'accessToken': access,
      'accessTokenExpiresAt': '2026-01-01T00:00:00Z',
      'refreshToken': refresh,
      'refreshTokenExpiresAt': '2026-02-01T00:00:00Z',
      'user': {
        'id': '11111111-1111-1111-1111-111111111111',
        'email': 'student@studyhive.test',
        'fullName': 'Test Student',
        'role': 'Student',
        'isActive': true,
        'createdAt': '2026-01-01T00:00:00Z',
      },
    };

/// A fake API: login issues access-1; [refreshOk] decides whether /refresh issues access-2;
/// data calls succeed only with the newest access token.
class _FakeApi {
  bool refreshOk = true;
  bool refreshRevalidates = true;
  int refreshCalls = 0;
  String validToken = 'access-1';
  Duration refreshDelay = Duration.zero;

  late final MockClient client = MockClient((request) async {
    final path = request.url.path;
    if (path == '/api/auth/login') {
      return _json(_tokens('access-1', 'refresh-1'));
    }
    if (path == '/api/auth/refresh') {
      refreshCalls++;
      await Future<void>.delayed(refreshDelay);
      if (!refreshOk) return _json({'title': 'Unauthorized'}, 401);
      if (refreshRevalidates) validToken = 'access-2';
      return _json(_tokens('access-2', 'refresh-2'));
    }
    if (request.headers['Authorization'] != 'Bearer $validToken') {
      return _json({'title': 'Unauthorized'}, 401);
    }
    return _json({'id': 'booking-1', 'objective': 'Revise databases'});
  });
}

Future<(AuthProvider, _FakeApi, _MemoryTokenStore)> _signedIn() async {
  final api = _FakeApi();
  final store = _MemoryTokenStore();
  final auth = AuthProvider(apiClient: ApiClient(client: api.client), tokenStore: store);
  await auth.login('student@studyhive.test', 'password');
  // The access token expires: the server now only accepts the refreshed one.
  api.validToken = 'access-2';
  return (auth, api, store);
}

void main() {
  test('a 401 refreshes the session once and retries the call', () async {
    final (auth, api, store) = await _signedIn();

    final booking = await auth.apiClient.get('/api/booking-requests/booking-1') as Map<String, dynamic>;

    expect(booking['objective'], 'Revise databases');
    expect(api.refreshCalls, 1);
    expect(auth.isAuthenticated, isTrue);
    expect(auth.apiClient.accessToken, 'access-2');
    expect(store.values['refresh_token'], 'refresh-2', reason: 'the new pair is saved');
  });

  test('a rejected refresh signs the student out with a reason', () async {
    final (auth, api, store) = await _signedIn();
    api.refreshOk = false;

    await expectLater(
      auth.apiClient.get('/api/booking-requests/booking-1'),
      throwsA(isA<ApiException>().having((e) => e.status, 'status', 401)),
    );

    expect(auth.isAuthenticated, isFalse);
    expect(auth.signedOutReason, AuthProvider.sessionExpiredMessage);
    expect(store.values, isEmpty, reason: 'both tokens are removed from the secure store');
  });

  test('concurrent 401s share one refresh', () async {
    final (auth, api, _) = await _signedIn();
    api.refreshDelay = const Duration(milliseconds: 50);

    final results = await Future.wait(List.generate(
        4, (_) => auth.apiClient.get('/api/booking-requests/booking-1')));

    expect(results, hasLength(4));
    expect(api.refreshCalls, 1);
  });

  test('a second 401 after a refresh is not retried again', () async {
    final (auth, api, _) = await _signedIn();
    api.refreshRevalidates = false;
    api.validToken = 'never-valid';

    await expectLater(
      auth.apiClient.get('/api/booking-requests/booking-1'),
      throwsA(isA<ApiException>().having((e) => e.status, 'status', 401)),
    );
    expect(api.refreshCalls, 1);
  });

  test('a wrong password does not try to refresh', () async {
    var refreshCalls = 0;
    final client = MockClient((request) async {
      if (request.url.path == '/api/auth/refresh') refreshCalls++;
      return _json({'title': 'Unauthorized', 'detail': 'The email or password is incorrect.'}, 401);
    });
    final auth = AuthProvider(apiClient: ApiClient(client: client), tokenStore: _MemoryTokenStore());

    await expectLater(auth.login('a@b.test', 'nope'), throwsA(isA<ApiException>()));
    expect(refreshCalls, 0);
  });

  test('a validation problem shows its first field message (C-12)', () async {
    final client = ApiClient(
      client: MockClient((_) async => _json({
            'type': 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
            'title': 'One or more validation errors occurred.',
            'status': 400,
            'errors': {
              'PreferredTimeTo': ['The end time must be after the start time.'],
            },
          }, 400)),
    );

    try {
      await client.post('/api/booking-requests', body: {});
      fail('expected an ApiException');
    } on ApiException catch (e) {
      expect(e.toString(), 'The end time must be after the start time.');
      expect(e.errors['PreferredTimeTo'], ['The end time must be after the start time.']);
    }
  });

  test('a slow server times out with a friendly message (C-12)', () async {
    final client = ApiClient(
      timeout: const Duration(milliseconds: 20),
      client: MockClient((_) async {
        await Future<void>.delayed(const Duration(milliseconds: 200));
        return _json({});
      }),
    );

    await expectLater(
      client.get('/api/rooms'),
      throwsA(isA<ApiException>().having((e) => e.toString(), 'message', ApiClient.timeoutMessage)),
    );
  });

  testWidgets('the sign-in screen says why the session ended', (tester) async {
    final (auth, api, _) = await _signedIn();
    api.refreshOk = false;
    await tester.runAsync(() async {
      try {
        await auth.apiClient.get('/api/booking-requests/booking-1');
      } on ApiException {
        // expected: the refresh was rejected
      }
    });

    await tester.pumpWidget(ChangeNotifierProvider.value(
      value: auth,
      child: const MaterialApp(home: LoginScreen()),
    ));

    expect(find.text(AuthProvider.sessionExpiredMessage), findsOneWidget);
  });
}
