import 'package:flutter/material.dart';
import 'package:permission_handler/permission_handler.dart';
import 'network/udp_voice_client.dart';
import 'audio/voice_manager.dart';

void main() {
  runApp(const MyApp());
}

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'GoodVoice Flutter',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: Colors.deepPurple),
        useMaterial3: true,
      ),
      home: const VoiceScreen(),
    );
  }
}

class VoiceScreen extends StatefulWidget {
  const VoiceScreen({super.key});

  @override
  State<VoiceScreen> createState() => _VoiceScreenState();
}

class _VoiceScreenState extends State<VoiceScreen> {
  final _serverHostController = TextEditingController(text: '127.0.0.1');
  final _serverPortController = TextEditingController(text: '50005');
  final _roomIdController = TextEditingController(text: '100');

  UdpVoiceClient? _client;
  VoiceManager? _voiceManager;

  bool _isConnected = false;
  bool _isRecording = false;

  @override
  void dispose() {
    _serverHostController.dispose();
    _serverPortController.dispose();
    _roomIdController.dispose();
    _voiceManager?.dispose();
    _client?.dispose();
    super.dispose();
  }

  Future<void> _connect() async {
    // Request microphone permission
    final status = await Permission.microphone.request();
    if (status != PermissionStatus.granted) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Microphone permission required')),
      );
      return;
    }

    final host = _serverHostController.text;
    final port = int.tryParse(_serverPortController.text) ?? 50005;
    final roomId = int.tryParse(_roomIdController.text) ?? 100;

    // Generate a random client ID between 1000 and 9999
    final clientId = 1000 + (DateTime.now().millisecondsSinceEpoch % 9000);

    _client = UdpVoiceClient(
      serverHost: host,
      serverPort: port,
      clientId: clientId,
    );

    await _client!.connect();
    await _client!.joinRoom(roomId);

    _voiceManager = VoiceManager(_client!);

    if (!mounted) return;
    setState(() {
      _isConnected = true;
    });
  }

  Future<void> _disconnect() async {
    if (_isRecording) {
      await _toggleRecording();
    }

    _voiceManager?.dispose();
    _client?.disconnect();

    if (!mounted) return;
    setState(() {
      _isConnected = false;
      _voiceManager = null;
      _client = null;
    });
  }

  Future<void> _toggleRecording() async {
    if (_voiceManager == null) return;

    if (_isRecording) {
      await _voiceManager!.stopRecording();
    } else {
      await _voiceManager!.startRecording();
    }

    if (!mounted) return;
    setState(() {
      _isRecording = !_isRecording;
    });
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('GoodVoice Flutter Client'),
        backgroundColor: Theme.of(context).colorScheme.inversePrimary,
      ),
      body: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            TextField(
              controller: _serverHostController,
              decoration: const InputDecoration(labelText: 'Server Host'),
              enabled: !_isConnected,
            ),
            TextField(
              controller: _serverPortController,
              decoration: const InputDecoration(labelText: 'Server Port'),
              keyboardType: TextInputType.number,
              enabled: !_isConnected,
            ),
            TextField(
              controller: _roomIdController,
              decoration: const InputDecoration(labelText: 'Room ID'),
              keyboardType: TextInputType.number,
              enabled: !_isConnected,
            ),
            const SizedBox(height: 24),
            ElevatedButton(
              onPressed: _isConnected ? _disconnect : _connect,
              child: Text(_isConnected ? 'Disconnect' : 'Connect'),
            ),
            const SizedBox(height: 24),
            if (_isConnected)
              ElevatedButton.icon(
                onPressed: _toggleRecording,
                icon: Icon(_isRecording ? Icons.mic_off : Icons.mic),
                label: Text(_isRecording ? 'Stop Recording' : 'Start Recording'),
                style: ElevatedButton.styleFrom(
                  backgroundColor: _isRecording ? Colors.red.shade100 : Colors.green.shade100,
                  padding: const EdgeInsets.all(16),
                ),
              ),
          ],
        ),
      ),
    );
  }
}
