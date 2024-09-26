FROM node:20-alpine

WORKDIR /app

COPY package.json ./
RUN \
    if [ -f yarn.lock ]; then yarn --frozen-lockfile; \
    elif [ -f package-lock.json ]; then npm ci; \
    elif [ -f pnpm-lock.yaml ]; then corepack enable pnpm && pnpm i; \
    # Allow install without lockfile, so example works even without Node.js installed locally
    else echo "Warning: Lockfile not found. It is recommended to commit lockfiles to version control." && yarn install; \
    fi


COPY src .
# COPY public ./public
# COPY next.config.js .
# COPY tsconfig.json .

# Next.js collects completely anonymous telemetry data about general usage. Learn more here: https://nextjs.org/telemetry
# Uncomment the following line to disable telemetry at run time
ENV NEXT_TELEMETRY_DISABLED 1

# Note: Don't expose ports here, Compose will handle that for us

# Start Next.js in development mode based on the preferred package manager
CMD \
    if [ -f yarn.lock ]; then yarn dev; \
    elif [ -f package-lock.json ]; then npm run dev; \
    elif [ -f pnpm-lock.yaml ]; then pnpm dev; \
    else npm run dev; \
    fi


# ### Dependencies ###
# FROM base AS deps
# RUN apk add --no-cache libc6-compat git

# # Setup pnpm environment
# ENV PNPM_HOME="/pnpm"
# ENV PATH="$PNPM_HOME:$PATH"
# RUN corepack enable
# RUN corepack prepare pnpm@latest --activate

# WORKDIR /src

# COPY package.json pnpm-lock.yaml ./
# RUN pnpm install --frozen-lockfile --prefer-frozen-lockfile

# # Builder
# FROM base AS builder

# RUN corepack enable
# RUN corepack prepare pnpm@latest --activate

# WORKDIR /src

# COPY --from=deps /src/node_modules ./node_modules
# COPY . .
# RUN pnpm build


# ### Production image runner ###
# FROM base AS runner

# # Set NODE_ENV to production
# ENV NODE_ENV production

# # Disable Next.js telemetry
# # Learn more here: https://nextjs.org/telemetry
# ENV NEXT_TELEMETRY_DISABLED 1

# # Set correct permissions for nextjs user and don't run as root
# RUN addgroup nodejs
# RUN adduser -SDH nextjs
# RUN mkdir .next
# RUN chown nextjs:nodejs .next

# # Automatically leverage output traces to reduce image size
# # https://nextjs.org/docs/advanced-features/output-file-tracing
# COPY --from=builder --chown=nextjs:nodejs /src/.next/standalone ./
# COPY --from=builder --chown=nextjs:nodejs /src/.next/static ./.next/static
# COPY --from=builder --chown=nextjs:nodejs /src/public ./public

# USER nextjs

# # Exposed port (for orchestrators and dynamic reverse proxies)
# EXPOSE 3000
# ENV PORT 3000
# ENV HOSTNAME "0.0.0.0"
# HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 CMD [ "wget", "-q0", "http://localhost:3000/health" ]

# # Run the nextjs app
# CMD ["node", "server.js"]