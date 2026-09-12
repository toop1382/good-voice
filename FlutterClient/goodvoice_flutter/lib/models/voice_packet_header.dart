import 'dart:typed_data';

class VoicePacketHeader {
  static const int headerSize = 28;

  final int packetType;
  final int roomId;
  final int clientId;
  final int sequenceNumber;
  final int sendTimestamp;
  final int payloadLength;

  VoicePacketHeader({
    required this.packetType,
    required this.roomId,
    required this.clientId,
    required this.sequenceNumber,
    required this.sendTimestamp,
    required this.payloadLength,
  });

  factory VoicePacketHeader.fromBytes(Uint8List buffer) {
    if (buffer.length < headerSize) {
      throw Exception('Buffer too small to read VoicePacketHeader');
    }

    final byteData = ByteData.sublistView(buffer);

    return VoicePacketHeader(
      packetType: byteData.getInt32(0, Endian.little),
      roomId: byteData.getInt32(4, Endian.little),
      clientId: byteData.getInt32(8, Endian.little),
      sequenceNumber: byteData.getUint32(12, Endian.little),
      sendTimestamp: byteData.getInt64(16, Endian.little),
      payloadLength: byteData.getInt32(24, Endian.little),
    );
  }

  Uint8List toBytes() {
    final buffer = Uint8List(headerSize);
    final byteData = ByteData.sublistView(buffer);

    byteData.setInt32(0, packetType, Endian.little);
    byteData.setInt32(4, roomId, Endian.little);
    byteData.setInt32(8, clientId, Endian.little);
    byteData.setUint32(12, sequenceNumber, Endian.little);
    byteData.setInt64(16, sendTimestamp, Endian.little);
    byteData.setInt32(24, payloadLength, Endian.little);

    return buffer;
  }
}
