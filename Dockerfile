FROM node:20-alpine AS builder

WORKDIR /build

# copy over source
COPY ./src/ ./

# remove these files as they cause problems with the build
RUN rm -rf .next 
RUN rm -rf node_modules
RUN rm -rf .env

# run npm install
RUN npm ci

# Next.js collects completely anonymous telemetry data about general usage. Learn more here: https://nextjs.org/telemetry
# Uncomment the following line to disable telemetry at run time
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

FROM node:20-alpine AS runner
USER node
WORKDIR /app
ENV PORT=3000
ENV HOSTNAME="0.0.0.0"
ENV NODE_ENV=production

COPY --from=builder /build/next.config.js ./
COPY --from=builder /build/public ./public
COPY --from=builder /build/package.json ./package.json

COPY --from=builder /build/.next/standalone ./
COPY --from=builder /build/.next/static ./.next/static

# HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 CMD [ "wget", "-q0", "http://localhost:3000/health" ]

# Run the nextjs app
EXPOSE 3000
CMD ["node", "server.js"]
