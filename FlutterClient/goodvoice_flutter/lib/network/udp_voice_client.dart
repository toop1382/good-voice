import 'dart:async';
import 'dart:io';
import 'package:flutter/foundation.dart';
import '../models/voice_packet_header.dart';
import '../models/packet_type.dart';

class UdpVoiceClient {
  RawDatagramSocket? _socket;
  final String serverHost;
  final int serverPort;
  final int clientId;
  int roomId = 0;

  bool get isConnected => _socket != null;
  int _sequenceNumber = 0;

  final _audioStreamController = StreamController<Uint8List>.broadcast();
  Stream<Uint8List> get onAudioReceived => _audioStreamController.stream;

  UdpVoiceClient({
    required this.serverHost,
    required this.serverPort,
    required this.clientId,
  });

  Future<void> connect() async {
    _socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
    _socket!.listen((RawSocketEvent event) {
      if (event == RawSocketEvent.read) {
        final datagram = _socket!.receive();
        if (datagram != null) {
          _handleDatagram(datagram);
        }
      }
    });

    // Send handshake
    _sendPacket(PacketType.handshake, Uint8List(0));
  }

  Future<void> joinRoom(int roomId) async {
    this.roomId = roomId;
    if (isConnected) {
      _sendPacket(PacketType.roomJoin, Uint8List(0));
    }
  }

  void sendAudio(Uint8List opusData) {
    if (isConnected && roomId != 0) {
      _sendPacket(PacketType.audio, opusData);
    }
  }

  void _sendPacket(int packetType, Uint8List payload) {
    if (_socket == null) return;

    _sequenceNumber++;
    final header = VoicePacketHeader(
      packetType: packetType,
      roomId: roomId,
      clientId: clientId,
      sequenceNumber: _sequenceNumber,
      sendTimestamp: DateTime.now().millisecondsSinceEpoch,
      payloadLength: payload.length,
    );

    final headerBytes = header.toBytes();
    final packet = Uint8List(headerBytes.length + payload.length);
    packet.setAll(0, headerBytes);
    packet.setAll(headerBytes.length, payload);

    try {
      final address = InternetAddress(serverHost);
      _socket!.send(packet, address, serverPort);
    } catch (e) {
      debugPrint('Error sending packet: $e');
    }
  }

  void _handleDatagram(Datagram datagram) {
    if (datagram.data.length < VoicePacketHeader.headerSize) return;

    try {
      final header = VoicePacketHeader.fromBytes(datagram.data);

      // We only care about audio packets
      if (header.packetType == PacketType.audio) {
        // Skip packets from ourselves (if server sends them back)
        if (header.clientId == clientId) return;

        final payload = Uint8List.sublistView(datagram.data, VoicePacketHeader.headerSize);
        _audioStreamController.add(payload);
      }
    } catch (e) {
      debugPrint('Error parsing datagram: $e');
    }
  }

  void disconnect() {
    _socket?.close();
    _socket = null;
  }

  void dispose() {
    disconnect();
    _audioStreamController.close();
  }
}
