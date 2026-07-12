import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:record/record.dart';
import 'package:flutter_opus/flutter_opus.dart';
import '../network/udp_voice_client.dart';

class VoiceManager {
  final UdpVoiceClient _client;
  final _audioRecorder = AudioRecorder();
  StreamSubscription? _recordSubscription;
  StreamSubscription? _clientSubscription;

  bool _isRecording = false;

  // Audio configuration
  static const int sampleRate = 48000;
  static const int channels = 1;
  static const int frameSizeInMs = 20;
  static const int pcmFrameSize = sampleRate * frameSizeInMs ~/ 1000; // 960 frames
  static const int pcmFrameBytes = pcmFrameSize * channels * 2; // 2 bytes per sample (16-bit)

  OpusEncoder? _encoder;
  OpusDecoder? _decoder;

  // Ring buffer for microphone input
  final List<int> _pcmBuffer = [];

  VoiceManager(this._client) {
    _initOpus();
  }

  void _initOpus() {
    _encoder = OpusEncoder.create(
      sampleRate: sampleRate,
      channels: channels,
      application: 2049, // OPUS_APPLICATION_VOIP
    );

    _decoder = OpusDecoder.create(
      sampleRate: sampleRate,
      channels: channels,
    );
  }

  Future<void> startRecording() async {
    if (_isRecording) return;

    if (await _audioRecorder.hasPermission()) {
      _pcmBuffer.clear(); // Reset buffer on start

      final stream = await _audioRecorder.startStream(
        const RecordConfig(
          encoder: AudioEncoder.pcm16bits,
          sampleRate: sampleRate,
          numChannels: channels,
        ),
      );

      _isRecording = true;

      _recordSubscription = stream.listen((data) {
        _processAudioData(data);
      });

      _clientSubscription = _client.onAudioReceived.listen((data) {
        _handleReceivedAudio(data);
      });
    }
  }

  void _processAudioData(Uint8List data) {
    if (_encoder == null) return;

    // Add raw byte data to our ring buffer
    _pcmBuffer.addAll(data);

    // Encode in chunks of exact frame size
    while (_pcmBuffer.length >= pcmFrameBytes) {
      final chunkBytes = _pcmBuffer.sublist(0, pcmFrameBytes);
      _pcmBuffer.removeRange(0, pcmFrameBytes);

      final chunkUint8 = Uint8List.fromList(chunkBytes);
      final int16Data = chunkUint8.buffer.asInt16List();

      try {
        final opusData = _encoder!.encode(int16Data, pcmFrameSize);
        if (opusData != null) {
          _client.sendAudio(opusData);
        }
      } catch (e) {
        debugPrint('Opus encoding error: $e');
      }
    }
  }

  void _handleReceivedAudio(Uint8List opusData) {
    if (_decoder == null) return;

    try {
      final pcmData = _decoder!.decode(opusData, pcmFrameSize);

      // Playback logic goes here
      // For now, we decode successfully but drop the PCM data since
      // simple audio players don't support streaming PCM out-of-the-box.
      if (pcmData != null && pcmData.isNotEmpty) {
          // Playback PCM data
      }
    } catch (e) {
      debugPrint('Opus decoding error: $e');
    }
  }

  Future<void> stopRecording() async {
    if (!_isRecording) return;

    await _recordSubscription?.cancel();
    await _clientSubscription?.cancel();
    await _audioRecorder.stop();
    _isRecording = false;
  }

  void dispose() {
    stopRecording();
    _audioRecorder.dispose();
    _encoder?.dispose();
    _decoder?.dispose();
  }
}
