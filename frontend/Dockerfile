FROM node:22-alpine AS build
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
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
