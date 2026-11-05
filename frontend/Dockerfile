FROM node:26-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# Empty = same-origin requests through the nginx proxy below.
ARG VITE_API_URL=""
ENV VITE_API_URL=$VITE_API_URL
# Set to "true" for real deployments to hide the demo credentials hint on the login page.
ARG VITE_HIDE_DEMO_LOGINS=""
ENV VITE_HIDE_DEMO_LOGINS=$VITE_HIDE_DEMO_LOGINS
RUN npm run build

FROM nginx:alpine
# Pick up Alpine security fixes that are released before a new nginx:alpine image is published.
RUN apk upgrade --no-cache
# The official image renders /etc/nginx/templates/*.template with envsubst at startup.
ENV NGINX_RESOLVER=127.0.0.11 \
    API_UPSTREAM=http://api:8080
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
