"""Measure Live TV channel starts against a Finly/Jellyfin server without a client.

For each channel it asks for playback info the way the Android TV app does (which opens the live stream on the
tuner), downloads the direct stream URL for a few seconds and reports:
  - time for the playback info call (tuning, probing)
  - time to the first byte of the stream, and bytes received after 1, 2, 3 ... seconds
  - whether the data is MPEG-TS (0x47 sync byte every 188 bytes) and which PIDs/stream types the PMT lists
Then it closes the live stream so the tuner is released.

Usage: python livetv_probe.py --server http://192.168.1.249:8097 --token TOKEN --user USER_ID [--channels 1,2,3] [--seconds 6]
"""
import argparse
import json
import sys
import time
import urllib.parse
import urllib.request

STREAM_TYPES = {0x01: 'MPEG-1 video', 0x02: 'MPEG-2 video', 0x03: 'MPEG-1 audio', 0x04: 'MPEG-2 audio', 0x06: 'PES private (AC3/subs/teletext)',
                0x0F: 'AAC', 0x11: 'AAC LATM', 0x1B: 'H.264', 0x24: 'HEVC', 0x81: 'AC3', 0x87: 'E-AC3', 0x05: 'private sections'}


def request(server, token, path, method='GET', body=None, timeout=60):
    headers = {'Authorization': f'MediaBrowser Client="Finly probe", Device="probe", DeviceId="finly-probe", Version="1.0", Token="{token}"'}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(server + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
    return json.loads(raw) if raw else None


def parse_ts(data):
    """Check TS sync and list the elementary streams from the first PAT/PMT."""
    start = data.find(b'\x47')
    while start != -1 and start + 188 * 3 < len(data) and not (data[start + 188] == 0x47 and data[start + 376] == 0x47):
        start = data.find(b'\x47', start + 1)
    if start == -1 or start + 188 * 3 >= len(data):
        return {'sync': False}
    packets = [data[i:i + 188] for i in range(start, len(data) - 187, 188)]
    bad = sum(1 for p in packets if p[0] != 0x47)
    pmt_pids, streams, pids = set(), {}, {}
    for p in packets:
        if p[0] != 0x47:
            continue
        pid = ((p[1] & 0x1F) << 8) | p[2]
        pids[pid] = pids.get(pid, 0) + 1
        pusi = p[1] & 0x40
        afc = (p[3] >> 4) & 3
        off = 4
        if afc in (2, 3):
            off += 1 + p[4]
        if not pusi or off >= 188:
            continue
        payload = p[off + 1 + p[off]:]  # skip pointer field
        if pid == 0 and payload[:1] == b'\x00':
            length = ((payload[1] & 0x0F) << 8) | payload[2]
            for i in range(8, 3 + length - 4, 4):
                prog = (payload[i] << 8) | payload[i + 1]
                if prog:
                    pmt_pids.add(((payload[i + 2] & 0x1F) << 8) | payload[i + 3])
        elif pid in pmt_pids and payload[:1] == b'\x02' and not streams:
            length = ((payload[1] & 0x0F) << 8) | payload[2]
            info_len = ((payload[10] & 0x0F) << 8) | payload[11]
            i = 12 + info_len
            while i + 5 <= 3 + length - 4:
                stype = payload[i]
                epid = ((payload[i + 1] & 0x1F) << 8) | payload[i + 2]
                es_len = ((payload[i + 3] & 0x0F) << 8) | payload[i + 4]
                streams[epid] = STREAM_TYPES.get(stype, hex(stype))
                i += 5 + es_len
    return {'sync': True, 'offset': start, 'packets': len(packets), 'bad': bad, 'pmt': sorted(pmt_pids), 'streams': streams,
            'packets_per_pid': {k: v for k, v in sorted(pids.items(), key=lambda kv: -kv[1])[:8]}}


def probe(server, token, user, channel, seconds):
    result = {'channel': channel['Number'], 'name': channel['Name']}
    t0 = time.time()
    info = request(server, token, f"/Items/{channel['Id']}/PlaybackInfo?userId={user}", 'POST', {
        'UserId': user, 'AutoOpenLiveStream': True, 'EnableDirectPlay': True, 'EnableDirectStream': True, 'EnableTranscoding': False,
        'DeviceProfile': {'DirectPlayProfiles': [{'Container': 'ts,mpegts', 'Type': 'Video', 'VideoCodec': 'mpeg2video,h264', 'AudioCodec': 'mp2,aac,ac3,aac_latm'}],
                          'TranscodingProfiles': [], 'CodecProfiles': [], 'SubtitleProfiles': []}})
    result['info_ms'] = int((time.time() - t0) * 1000)
    source = info['MediaSources'][0]
    live_id = source.get('LiveStreamId')
    result['server_streams'] = [f"{s.get('Type')}:{s.get('Codec')}:{s.get('Width') or ''}x{s.get('Height') or ''}" for s in source.get('MediaStreams', [])]
    url = f"{server}/Videos/{channel['Id']}/stream?container=ts&static=true&mediaSourceId={urllib.parse.quote(source['Id'])}"
    if live_id:
        url += f"&liveStreamId={live_id}"
    try:
        req = urllib.request.Request(url, headers={'Authorization': f'MediaBrowser Token="{token}"'})
        t1 = time.time()
        buf = bytearray()
        marks = {}
        with urllib.request.urlopen(req, timeout=20) as r:
            result['http'] = r.status
            result['content_type'] = r.headers.get('Content-Type')
            while time.time() - t1 < seconds:
                chunk = r.read1(65536) if hasattr(r, 'read1') else r.read(65536)
                if not chunk:
                    result['ended_early'] = True
                    break
                if not buf:
                    result['ttfb_ms'] = int((time.time() - t1) * 1000)
                buf += chunk
                second = int(time.time() - t1)
                marks.setdefault(second, len(buf))
        result['bytes'] = len(buf)
        result['kb_by_second'] = [marks[k] // 1024 for k in sorted(marks)]
        result['ts'] = parse_ts(bytes(buf[:2_000_000]))
    except Exception as e:  # keep going with the other channels
        result['stream_error'] = repr(e)
    finally:
        if live_id:
            try:
                request(server, token, f"/LiveStreams/Close?liveStreamId={live_id}", 'POST')
            except Exception as e:
                result['close_error'] = repr(e)
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--server', required=True)
    ap.add_argument('--token', required=True)
    ap.add_argument('--user', required=True)
    ap.add_argument('--channels', default='')
    ap.add_argument('--seconds', type=float, default=6)
    ap.add_argument('--pause', type=float, default=2)
    args = ap.parse_args()

    channels = request(args.server, args.token, f"/LiveTv/Channels?userId={args.user}&sortBy=SortName")['Items']
    wanted = [c.strip() for c in args.channels.split(',') if c.strip()]
    if wanted:
        channels = [c for c in channels if c.get('Number') in wanted]
    for channel in channels:
        print(json.dumps(probe(args.server, args.token, args.user, channel, args.seconds)), flush=True)
        time.sleep(args.pause)


if __name__ == '__main__':
    sys.exit(main())
