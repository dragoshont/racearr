FROM python:3.12-slim

LABEL org.opencontainers.image.title="racearr" \
      org.opencontainers.image.description="Aggressive download racer + pickup/speed SLAs for Radarr/Sonarr + qBittorrent" \
      org.opencontainers.image.source="https://github.com/dragoshont/racearr" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app
# Stdlib-only — no dependencies to install.
COPY racearr.py /app/racearr.py

RUN useradd -u 1000 -m racearr
USER 1000

ENV PYTHONUNBUFFERED=1 \
    HEALTH_PORT=9797
EXPOSE 9797

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=4 \
  CMD python -c "import os,urllib.request; urllib.request.urlopen('http://localhost:%s/healthz' % os.environ.get('HEALTH_PORT','9797'), timeout=5)"

CMD ["python", "-u", "/app/racearr.py"]
