FROM nginx:alpine

# Copy site files into nginx www folder
COPY . /usr/share/nginx/html

# Copy custom nginx configuration to enable proxying /api to proxy service
COPY nginx.conf /etc/nginx/conf.d/default.conf

CMD ["nginx", "-g", "daemon off;"]
