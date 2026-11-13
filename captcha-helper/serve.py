#!/usr/bin/env python3
"""Local static preview. Deploy index.html on any HTTPS static host in production."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

parser = argparse.ArgumentParser()
parser.add_argument('--bind', default='127.0.0.1')
parser.add_argument('--port', type=int, default=18130)
args = parser.parse_args()
document = Path(__file__).with_name('index.html').read_bytes()

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if urlsplit(self.path).path not in ('/', '/index.html'):
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Referrer-Policy', 'no-referrer')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Content-Length', str(len(document)))
        self.end_headers()
        self.wfile.write(document)

    def log_message(self, *_):
        pass  # Verification URLs contain short-lived challenge parameters.

print(f'Ticket helper: http://{args.bind}:{args.port}/', flush=True)
ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()
