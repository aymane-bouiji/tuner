FROM node:20-alpine
# FFmpeg gives built-in subtitles, audio languages and MKV / AC-3 playback
RUN apk add --no-cache ffmpeg
WORKDIR /app
COPY server.js package.json ./
COPY public ./public
ENV HOST=0.0.0.0 PORT=8080 NODE_ENV=production
EXPOSE 8080
USER node
HEALTHCHECK --interval=30s --timeout=5s CMD wget -qO- http://127.0.0.1:8080/features >/dev/null || exit 1
CMD ["node", "server.js"]
