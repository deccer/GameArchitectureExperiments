#version 330 core
#extension GL_ARB_explicit_uniform_location:enable

out gl_PerVertex
{
    vec4 gl_Position;
};

layout(location=0)in vec3 i_position;
layout(location=1)in vec3 i_color;

layout(location=0)uniform mat4 u_model;
layout(location=1)uniform mat4 u_view;
layout(location=2)uniform mat4 u_projection;

out vec4 ps_vertex_color;

void main()
{
    gl_Position=u_projection*u_view*u_model*vec4(i_position,1.);
    ps_vertex_color=vec4(i_color,1.);
}