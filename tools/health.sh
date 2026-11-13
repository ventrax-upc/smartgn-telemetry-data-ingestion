#!/bin/bash
exec 3<>/dev/tcp/127.0.0.1/${PORT:-8085} || exit 1
printf 'GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
cat <&3 | grep -q 'UP'
